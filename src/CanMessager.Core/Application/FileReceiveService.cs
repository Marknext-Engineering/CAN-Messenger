// =====================================================================
//  FileReceiveService — CMAP 파일 수신 서비스
//  개요   : HELLO 응답→META 수신(수락 정책)→DATA 수신/블록 ACK→FIN 시 SHA-256 검증.
//  핵심   : - IncomingFile 이벤트로 수락 여부/저장 경로/파일명 결정
//           - 임시파일(*.cmpart)에 청크 오프셋 기록 → 검증 성공 후 최종 이름으로 rename
//           - ACK_REQUEST 수신 시 현재 블록 비트맵 ACK 송신, 블록 완료 시 base 전진
//           - 파일명 sanitize(경로 탈출 방지)
//  추후 개선:
//           - 이어받기(Resume): 임시파일/수신 비트맵 보존하여 RESUME로 재개 — 현재 미구현
//           - 검증은 수신 완료 후 전체 재해시 → 스트리밍 해시로 메모리/시간 절감 여지
//           - 동시 다중 전송 세션(TransferId별 큐) 미지원(1세션 가정)
// =====================================================================
using System.Diagnostics;
using System.Security.Cryptography;
using CanMessager.Core.Transport;
using Microsoft.Win32.SafeHandles;

namespace CanMessager.Core.Application;

/// <summary>
/// CMAP 파일 수신 서비스. HELLO에 응답하고 META를 받아 (정책 확인 후) DATA를 수신,
/// 블록 단위로 ACK를 보내고 FIN 시 SHA-256을 검증한다. docs/02 5~6장.
/// </summary>
public sealed class FileReceiveService
{
    private readonly IIsoTpChannel _channel;
    private readonly CmapConfig _config;
    private readonly string _defaultSaveDir;

    public FileReceiveService(IIsoTpChannel channel, string defaultSaveDir, CmapConfig? config = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _defaultSaveDir = defaultSaveDir ?? throw new ArgumentNullException(nameof(defaultSaveDir));
        _config = config ?? new CmapConfig();
    }

    /// <summary>META 수신 시 발생. Accept/SavePath/RejectReason을 설정해 수락 여부를 결정.</summary>
    public event EventHandler<IncomingFileEventArgs>? IncomingFile;
    public event EventHandler<FileTransferProgress>? Progress;
    public event EventHandler<FileReceivedEventArgs>? FileReceived;

    /// <summary>들어오는 전송 1건을 끝까지 처리한다 (HELLO 대기 → 수신 완료/실패).</summary>
    public async Task ReceiveOneAsync(CancellationToken ct = default)
    {
        using var inbox = new CmapInbox(_channel);

        // ---- HELLO 대기 (무기한, 취소 시까지) ----
        var helloEnv = await inbox.ReceiveTypeAsync(CmapMsgType.Hello, Timeout.Infinite, ct).ConfigureAwait(false);
        if (helloEnv is null) return;
        var hello = CmapCodec.DecodeHello(helloEnv.Value.Body.Span);
        uint transferId = helloEnv.Value.Header.TransferId;

        // 협상: 청크는 송신측 선호 사용(여기선 그대로 수락), 윈도우는 양측 최소.
        uint agreedChunk = hello.PreferredChunk;
        ushort agreedWindow = Math.Min(hello.PreferredWindow, _config.WindowSize);
        byte cap = (byte)CmapFlags.ChunkCrc;
        await _channel.SendAsync(
            CmapCodec.EncodeHelloAck(transferId, new HelloAckMessage(CmapConstants.Version, agreedChunk, agreedWindow, cap)),
            ct).ConfigureAwait(false);

        // ---- META 대기 ----
        var metaEnv = await inbox.ReceiveTypeAsync(CmapMsgType.Meta, _config.MetaTimeoutMs, ct).ConfigureAwait(false);
        if (metaEnv is null) return;
        var meta = CmapCodec.DecodeMeta(metaEnv.Value.Body.Span);

        // ---- 수락 정책 ----
        var args = new IncomingFileEventArgs
        {
            TransferId = transferId,
            FileName = SanitizeFileName(meta.FileName),
            TotalBytes = meta.TotalSize
        };
        IncomingFile?.Invoke(this, args);

        if (!args.Accept)
        {
            await _channel.SendAsync(
                CmapCodec.EncodeMetaAck(transferId, new MetaAckMessage(MetaAckStatus.Reject, 0, args.RejectReason)),
                ct).ConfigureAwait(false);
            return;
        }

        string finalPath = args.SavePath ?? Path.Combine(_defaultSaveDir, args.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        string tempPath = finalPath + ".cmpart";

        await _channel.SendAsync(
            CmapCodec.EncodeMetaAck(transferId, new MetaAckMessage(MetaAckStatus.Accept, 0, CmapReason.None)),
            ct).ConfigureAwait(false);

        // ---- DATA 수신 루프 ----
        int chunkSize = (int)meta.ChunkSize;
        uint totalChunks = meta.TotalChunks;
        ushort window = meta.WindowSize == 0 ? agreedWindow : meta.WindowSize;

        var received = new bool[totalChunks];
        long bytesReceived = 0;
        uint blockBase = 0;
        var sw = Stopwatch.StartNew();
        long lastProgressTick = 0;

        using (var handle = File.OpenHandle(tempPath, FileMode.Create, FileAccess.Write))
        {
            if (totalChunks > 0)
                RandomAccess.SetLength(handle, meta.TotalSize);

            while (CountRemaining(received) > 0)
            {
                var env = await inbox.ReceiveAsync(_config.AckTimeoutMs, ct).ConfigureAwait(false);
                if (env is null)
                    throw new CmapException(CmapReason.Timeout, "DATA 수신 타임아웃.");

                var header = env.Value.Header;
                if (header.TransferId != transferId) continue;

                switch (header.Type)
                {
                    case CmapMsgType.Data:
                    {
                        var data = CmapCodec.DecodeData(header.Flags, env.Value.Body);
                        if (data.ChunkIndex >= totalChunks) break;

                        // CRC 검증 (옵션).
                        if (data.HasCrc && Crc32.Compute(data.Data.Span) != data.Crc32)
                            break; // 손상 → 비트맵에 마크 안 함 → 재전송 유발

                        if (!received[data.ChunkIndex])
                        {
                            long offset = (long)data.ChunkIndex * chunkSize;
                            RandomAccess.Write(handle, data.Data.Span, offset);
                            received[data.ChunkIndex] = true;
                            bytesReceived += data.Data.Length;
                        }

                        MaybeRaiseProgress(transferId, args.FileName, bytesReceived, meta.TotalSize,
                            sw, ref lastProgressTick, force: false);

                        if (header.Flags.HasFlag(CmapFlags.AckRequest))
                        {
                            await SendBlockAckAsync(transferId, blockBase, window, totalChunks, received, ct)
                                .ConfigureAwait(false);
                            if (BlockComplete(received, blockBase, window, totalChunks))
                                blockBase += window;
                        }
                        break;
                    }

                    case CmapMsgType.Fin:
                    {
                        // 모든 청크 수신 확인.
                        if (CountRemaining(received) > 0)
                        {
                            // 누락분 ACK로 통지 (현재 블록 기준).
                            await SendBlockAckAsync(transferId, blockBase, window, totalChunks, received, ct)
                                .ConfigureAwait(false);
                            break;
                        }
                        goto finished;
                    }

                    case CmapMsgType.Abort:
                        TryDelete(tempPath);
                        return;
                }
            }

            finished: ;
        }

        // ---- 무결성 검증 ----
        byte[] actual;
        await using (var vs = File.OpenRead(tempPath))
            actual = await SHA256.HashDataAsync(vs, ct).ConfigureAwait(false);

        bool ok = actual.AsSpan().SequenceEqual(meta.FileSha256);

        // FIN 수신을 한 번 더 기다려 FIN_ACK로 응답 (FIN이 위 루프 종료를 유발한 경우 즉시 응답).
        await _channel.SendAsync(
            CmapCodec.EncodeFinAck(transferId,
                new FinAckMessage(ok ? FinResult.Success : FinResult.HashMismatch, 0)),
            ct).ConfigureAwait(false);

        if (ok)
        {
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(tempPath, finalPath);
            MaybeRaiseProgress(transferId, args.FileName, meta.TotalSize, meta.TotalSize,
                sw, ref lastProgressTick, force: true);
            FileReceived?.Invoke(this, new FileReceivedEventArgs
            {
                TransferId = transferId,
                FileName = args.FileName,
                SavedPath = finalPath,
                TotalBytes = meta.TotalSize,
                HashVerified = true
            });
        }
        else
        {
            TryDelete(tempPath);
            throw new CmapException(CmapReason.HashMismatch, "수신 파일 SHA-256 불일치.");
        }
    }

    private Task SendBlockAckAsync(uint transferId, uint blockBase, ushort window, uint totalChunks,
        bool[] received, CancellationToken ct)
    {
        ushort bitCount = (ushort)Math.Min(window, totalChunks - blockBase);
        int k = (bitCount + 7) / 8;
        var bitmap = new byte[k];
        for (int bit = 0; bit < bitCount; bit++)
        {
            uint ci = blockBase + (uint)bit;
            if (received[ci]) bitmap[bit / 8] |= (byte)(1 << (bit % 8));
        }
        return _channel.SendAsync(CmapCodec.EncodeAck(transferId, isNak: false, blockBase, bitCount, bitmap), ct);
    }

    private static bool BlockComplete(bool[] received, uint blockBase, ushort window, uint totalChunks)
    {
        uint end = Math.Min(blockBase + window, totalChunks);
        for (uint i = blockBase; i < end; i++)
            if (!received[i]) return false;
        return true;
    }

    private static int CountRemaining(bool[] received)
    {
        int n = 0;
        foreach (var r in received) if (!r) n++;
        return n;
    }

    private static string SanitizeFileName(string name)
    {
        // 경로 분리자 제거 — 디렉터리 탈출 방지.
        var justName = Path.GetFileName(name);
        foreach (var c in Path.GetInvalidFileNameChars())
            justName = justName.Replace(c, '_');
        return string.IsNullOrWhiteSpace(justName) ? "received.bin" : justName;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    private void MaybeRaiseProgress(uint transferId, string fileName, long transferred, long total,
        Stopwatch sw, ref long lastTick, bool force)
    {
        long now = sw.ElapsedMilliseconds;
        if (!force && _config.ProgressIntervalMs > 0 && now - lastTick < _config.ProgressIntervalMs)
            return;
        lastTick = now;

        double seconds = Math.Max(now / 1000.0, 0.001);
        double bps = transferred / seconds;
        var eta = bps > 1 ? TimeSpan.FromSeconds((total - transferred) / bps) : TimeSpan.Zero;

        Progress?.Invoke(this, new FileTransferProgress
        {
            TransferId = transferId,
            FileName = fileName,
            BytesTransferred = transferred,
            TotalBytes = total,
            BytesPerSecond = bps,
            Eta = eta
        });
    }
}
