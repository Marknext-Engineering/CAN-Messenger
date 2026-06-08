// =====================================================================
//  FileSendService — CMAP 파일 송신 서비스
//  개요   : ISO-TP 채널 위에서 HELLO→META→(블록 DATA/ACK)→FIN 절차로 파일 전송.
//  핵심   : - HandshakeAsync : HELLO/HELLO_ACK로 청크/윈도우 협상
//           - SendMetaAsync  : 파일명/크기/SHA-256/총청크 전달, 수락/이어받기 확인
//           - 블록 루프      : 윈도우 단위 DATA 전송→ACK 비트맵→누락분 재전송
//           - 마지막 DATA에 ACK_REQUEST 플래그(수신측 즉시 ACK 유도)
//  추후 개선:
//           - 파일 SHA-256를 전송 시작 전 1회 전체 계산(대용량시 시작 지연) → 스트리밍 해시 여지
//           - 청크 재전송이 블록 단위로 전체 반복 → 청크 단위 선별 재전송 최적화 가능
//           - 취소(CancellationToken) 시 ABORT PDU 송신은 미구현(상위에서 연결 해제로 처리)
// =====================================================================
using System.Diagnostics;
using System.Security.Cryptography;
using CanMessager.Core.Transport;
using Microsoft.Win32.SafeHandles;

namespace CanMessager.Core.Application;

/// <summary>
/// CMAP 파일 송신 서비스. ISO-TP 채널 위에서 HELLO→META→(블록 DATA/ACK)→FIN 절차로
/// 파일을 전송한다. docs/02 5~6장.
/// </summary>
public sealed class FileSendService
{
    private readonly IIsoTpChannel _channel;
    private readonly CmapConfig _config;

    public FileSendService(IIsoTpChannel channel, CmapConfig? config = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _config = config ?? new CmapConfig();
    }

    public event EventHandler<FileTransferProgress>? Progress;

    public async Task SendFileAsync(string path, CancellationToken ct = default)
    {
        using var inbox = new CmapInbox(_channel);

        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("File not found.", path);

        long totalSize = info.Length;
        string fileName = info.Name;
        uint transferId = unchecked((uint)Random.Shared.NextInt64());

        byte[] sha;
        await using (var hs = File.OpenRead(path))
            sha = await SHA256.HashDataAsync(hs, ct).ConfigureAwait(false);

        // ---- HELLO / 협상 ----
        int chunkSize = _config.ChunkSize;
        ushort window = _config.WindowSize;

        var helloAck = await HandshakeAsync(inbox, transferId, chunkSize, window, ct).ConfigureAwait(false);
        chunkSize = (int)helloAck.AgreedChunk;
        window = helloAck.AgreedWindow;
        bool useCrc = _config.UseChunkCrc && (helloAck.CapFlags & (byte)CmapFlags.ChunkCrc) != 0;

        uint totalChunks = totalSize == 0 ? 0 : (uint)((totalSize + chunkSize - 1) / chunkSize);

        // ---- META / META_ACK ----
        var meta = new MetaMessage((uint)chunkSize, totalSize, totalChunks, sha, window, fileName);
        var metaAck = await SendMetaAsync(inbox, transferId, meta, ct).ConfigureAwait(false);

        if (metaAck.Status == MetaAckStatus.Reject)
            throw new CmapException(metaAck.RejectReason, $"수신측 거절: {metaAck.RejectReason}");

        uint baseIdx = metaAck.Status == MetaAckStatus.ResumeAccept ? metaAck.ResumeChunk : 0;

        // ---- 블록 DATA/ACK 루프 ----
        var acked = new bool[totalChunks];
        for (uint i = 0; i < baseIdx && i < totalChunks; i++) acked[i] = true;

        long bytesAcked = (long)baseIdx * chunkSize;
        if (bytesAcked > totalSize) bytesAcked = totalSize;

        var sw = Stopwatch.StartNew();
        long lastProgressTick = 0;
        var buffer = new byte[chunkSize];
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        while (baseIdx < totalChunks)
        {
            uint windowEnd = Math.Min(baseIdx + window, totalChunks);
            int retries = 0;

            while (true)
            {
                // 미확인 청크 수집.
                var toSend = new List<uint>();
                for (uint i = baseIdx; i < windowEnd; i++)
                    if (!acked[i]) toSend.Add(i);
                if (toSend.Count == 0) break;

                // DATA 전송 (마지막에 AckRequest).
                for (int k = 0; k < toSend.Count; k++)
                {
                    ct.ThrowIfCancellationRequested();
                    uint ci = toSend[k];
                    int len = ReadChunk(handle, ci, chunkSize, totalSize, buffer);
                    bool ackReq = k == toSend.Count - 1;
                    var pdu = CmapCodec.EncodeData(transferId, ci, buffer.AsSpan(0, len), useCrc, ackReq);
                    await _channel.SendAsync(pdu, ct).ConfigureAwait(false);
                }

                // ACK 대기.
                var env = await inbox.ReceiveTypeAsync(CmapMsgType.Ack, _config.AckTimeoutMs, ct).ConfigureAwait(false);
                if (env is null)
                {
                    if (++retries > _config.MaxRetriesPerBlock)
                        throw new CmapException(CmapReason.Timeout, "블록 ACK 타임아웃, 재시도 초과.");
                    continue;
                }

                var ack = CmapCodec.DecodeAck(env.Value.Body.Span);
                for (int bit = 0; bit < ack.BitCount; bit++)
                {
                    uint ci = ack.BlockBaseIndex + (uint)bit;
                    if (ci >= totalChunks) break;
                    bool got = (ack.Bitmap[bit / 8] & (1 << (bit % 8))) != 0;
                    if (got && !acked[ci])
                    {
                        acked[ci] = true;
                        bytesAcked += ChunkLength(ci, totalChunks, chunkSize, totalSize);
                    }
                }

                MaybeRaiseProgress(transferId, fileName, bytesAcked, totalSize, sw, ref lastProgressTick, force: false);

                bool full = true;
                for (uint i = baseIdx; i < windowEnd; i++)
                    if (!acked[i]) { full = false; break; }
                if (full) break;

                if (++retries > _config.MaxRetriesPerBlock)
                    throw new CmapException(CmapReason.Timeout, "블록 미완료, 재시도 초과.");
            }

            baseIdx = windowEnd;
        }

        // ---- FIN / FIN_ACK ----
        await _channel.SendAsync(CmapCodec.EncodeFin(transferId, totalChunks), ct).ConfigureAwait(false);
        var finEnv = await inbox.ReceiveTypeAsync(CmapMsgType.FinAck, _config.FinTimeoutMs, ct).ConfigureAwait(false);
        if (finEnv is null)
            throw new CmapException(CmapReason.Timeout, "FIN_ACK 타임아웃.");

        var finAck = CmapCodec.DecodeFinAck(finEnv.Value.Body.Span);
        if (finAck.Result != FinResult.Success)
            throw new CmapException(
                finAck.Result == FinResult.HashMismatch ? CmapReason.HashMismatch : CmapReason.None,
                $"수신측 검증 실패: {finAck.Result} (missing={finAck.MissingCount})");

        MaybeRaiseProgress(transferId, fileName, totalSize, totalSize, sw, ref lastProgressTick, force: true);
    }

    private async Task<HelloAckMessage> HandshakeAsync(
        CmapInbox inbox, uint transferId, int chunkSize, ushort window, CancellationToken ct)
    {
        byte cap = (byte)CmapFlags.ChunkCrc;
        var hello = new HelloMessage(
            CmapConstants.Version, ushort.MaxValue, (uint)chunkSize, window, cap);
        var pdu = CmapCodec.EncodeHello(transferId, hello);

        for (int attempt = 0; attempt <= _config.MaxHandshakeRetries; attempt++)
        {
            await _channel.SendAsync(pdu, ct).ConfigureAwait(false);
            var env = await inbox.ReceiveTypeAsync(CmapMsgType.HelloAck, _config.HelloTimeoutMs, ct)
                .ConfigureAwait(false);
            if (env is not null)
                return CmapCodec.DecodeHelloAck(env.Value.Body.Span);
        }
        throw new CmapException(CmapReason.Timeout, "HELLO_ACK 타임아웃, 재시도 초과.");
    }

    private async Task<MetaAckMessage> SendMetaAsync(
        CmapInbox inbox, uint transferId, MetaMessage meta, CancellationToken ct)
    {
        var pdu = CmapCodec.EncodeMeta(transferId, meta);
        for (int attempt = 0; attempt <= _config.MaxHandshakeRetries; attempt++)
        {
            await _channel.SendAsync(pdu, ct).ConfigureAwait(false);
            var env = await inbox.ReceiveTypeAsync(CmapMsgType.MetaAck, _config.MetaTimeoutMs, ct)
                .ConfigureAwait(false);
            if (env is not null)
                return CmapCodec.DecodeMetaAck(env.Value.Body.Span);
        }
        throw new CmapException(CmapReason.Timeout, "META_ACK 타임아웃, 재시도 초과.");
    }

    private static int ReadChunk(SafeFileHandle handle, uint chunkIndex, int chunkSize, long totalSize, byte[] buffer)
    {
        long offset = (long)chunkIndex * chunkSize;
        int len = (int)Math.Min(chunkSize, totalSize - offset);
        int read = 0;
        while (read < len)
        {
            int n = RandomAccess.Read(handle, buffer.AsSpan(read, len - read), offset + read);
            if (n == 0) break;
            read += n;
        }
        return read;
    }

    private static long ChunkLength(uint chunkIndex, uint totalChunks, int chunkSize, long totalSize)
        => chunkIndex == totalChunks - 1 ? totalSize - (long)(totalChunks - 1) * chunkSize : chunkSize;

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
