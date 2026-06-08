// =====================================================================
//  IsoTpChannel — ISO-TP(ISO 15765-2) 전송 계층 핵심 구현
//  개요   : 가변 길이 메시지를 CAN-FD 64byte 프레임으로 세그먼트/재조립.
//           송신/수신 상태머신 + Flow Control(FC) + 타이밍(N_Bs/N_Cr/STmin).
//  핵심   : - SenderLoopAsync : SF 또는 FF→FC→CF... 순서로 한 메시지 전송
//           - ReceiverLoopAsync: SF 즉시 처리 / FF 수신 후 CF 재조립
//           - _fcChannel(흐름제어) / _rxDataChannel(데이터) 로 수신 라우팅
//           - SendRawAsync : 모든 프레임을 CAN-FD 유효 길이로 패딩 후 송신
//  추후 개선:
//           - 현재 BlockSize 기본 0(메시지당 FC 1회). 대용량시 BS>0 흐름제어 튜닝 여지
//           - STmin>0(마이크로초 단위) 정밀 지연은 Task.Delay 해상도 한계 → 필요시 스핀
//           - 송신 실패(Abort) 시 부분 재전송은 상위 CMAP 블록 ACK에 위임(레이어 분리)
// =====================================================================
using System.Threading.Channels;
using CanMessager.Hal;

namespace CanMessager.Core.Transport;

/// <summary>
/// ISO-TP(ISO 15765-2) CAN-FD 채널 구현.
/// 송신/수신 상태머신, Flow Control, 타이밍을 docs/01_ISO-TP_Layer_Design.md 4~6장에 따라 처리한다.
/// </summary>
public sealed class IsoTpChannel : IIsoTpChannel
{
    private readonly ICanDriver _driver;
    private readonly Channel<SendItem> _sendQueue = Channel.CreateUnbounded<SendItem>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<IsoTpFrame> _fcChannel = Channel.CreateUnbounded<IsoTpFrame>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<IsoTpFrame> _rxDataChannel = Channel.CreateUnbounded<IsoTpFrame>(
        new UnboundedChannelOptions { SingleReader = true });

    private CancellationTokenSource? _cts;
    private Task? _senderTask;
    private Task? _receiverTask;

    public IsoTpChannel(ICanDriver driver, IsoTpConfig config)
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        Config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public IsoTpConfig Config { get; }

    public event EventHandler<IsoTpMessageReceivedEventArgs>? MessageReceived;
    public event EventHandler<IsoTpErrorEventArgs>? ErrorOccurred;
    public event EventHandler<IsoTpProgressEventArgs>? Progress;

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_cts is not null) return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        _driver.FrameReceived += OnFrameReceived;
        _senderTask = Task.Run(() => SenderLoopAsync(token), token);
        _receiverTask = Task.Run(() => ReceiverLoopAsync(token), token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        _driver.FrameReceived -= OnFrameReceived;
        _cts.Cancel();

        try
        {
            await Task.WhenAll(_senderTask ?? Task.CompletedTask, _receiverTask ?? Task.CompletedTask)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* expected */ }

        _cts.Dispose();
        _cts = null;
    }

    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (_cts is null) throw new InvalidOperationException("Channel is not started.");
        var item = new SendItem(payload, ct);
        await _sendQueue.Writer.WriteAsync(item, ct).ConfigureAwait(false);
        await item.Completion.Task.ConfigureAwait(false);
    }

    // ===================== 수신 라우팅 =====================

    private void OnFrameReceived(object? sender, CanFdFrameReceivedEventArgs e)
    {
        var frame = e.Frame;
        if (frame.CanId != Config.RxCanId) return;

        var parsed = IsoTpPci.Decode(frame.Data);
        if (parsed.Type == IsoTpFrameType.FlowControl)
            _fcChannel.Writer.TryWrite(parsed);
        else
            _rxDataChannel.Writer.TryWrite(parsed);
    }

    // ===================== 송신 상태머신 =====================

    private async Task SenderLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var item in _sendQueue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, item.UserToken);
                try
                {
                    await SendOneAsync(item.Payload, linked.Token).ConfigureAwait(false);
                    item.Completion.TrySetResult();
                }
                catch (OperationCanceledException) when (item.UserToken.IsCancellationRequested)
                {
                    item.Completion.TrySetCanceled(item.UserToken);
                }
                catch (Exception ex)
                {
                    item.Completion.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task SendOneAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        long total = payload.Length;

        // SF로 충분한 경우.
        if (payload.Length <= IsoTpPci.SfMaxData)
        {
            await SendRawAsync(IsoTpPci.EncodeSingleFrame(payload.Span), ct).ConfigureAwait(false);
            RaiseProgress(IsoTpDirection.Send, total, total);
            return;
        }

        // 이전 전송에서 남은 stale FC 제거.
        while (_fcChannel.Reader.TryRead(out _)) { }

        // First Frame.
        var ff = IsoTpPci.EncodeFirstFrame(total, payload.Span, out int consumed);
        await SendRawAsync(ff, ct).ConfigureAwait(false);
        int offset = consumed;
        RaiseProgress(IsoTpDirection.Send, offset, total);

        // 첫 FC 대기.
        var (bs, stMin) = await AwaitFlowControlAsync(ct).ConfigureAwait(false);

        byte sn = 1;
        int sentInBlock = 0;
        var stDelay = IsoTpPci.DecodeSTmin(stMin);

        while (offset < payload.Length)
        {
            var cf = IsoTpPci.EncodeConsecutiveFrame(payload.Span, offset, sn, out int cfConsumed,
                Config.PaddingByte, padToValidLength: false);
            await SendRawAsync(cf, ct).ConfigureAwait(false);

            offset += cfConsumed;
            sn = (byte)((sn + 1) & 0x0F);
            sentInBlock++;
            RaiseProgress(IsoTpDirection.Send, offset, total);

            if (offset >= payload.Length) break;

            if (bs != 0 && sentInBlock == bs)
            {
                (bs, stMin) = await AwaitFlowControlAsync(ct).ConfigureAwait(false);
                stDelay = IsoTpPci.DecodeSTmin(stMin);
                sentInBlock = 0;
            }
            else if (stDelay > TimeSpan.Zero)
            {
                await Task.Delay(stDelay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>FC를 기다려 CTS면 (BlockSize, STmin)을 반환. WAIT은 재대기, OVFLW/타임아웃은 예외.</summary>
    private async Task<(byte blockSize, byte stMin)> AwaitFlowControlAsync(CancellationToken ct)
    {
        int waitCount = 0;
        while (true)
        {
            var fc = await ReadFrameWithTimeoutAsync(_fcChannel.Reader, Config.N_Bs_ms, ct).ConfigureAwait(false);
            if (fc is null)
                throw Fail(IsoTpError.TimeoutBs, IsoTpDirection.Send, "FC 수신 타임아웃 (N_Bs).");

            switch (fc.Value.FlowStatus)
            {
                case FlowStatus.ContinueToSend:
                    return (fc.Value.BlockSize, fc.Value.STmin);
                case FlowStatus.Wait:
                    if (++waitCount > Config.MaxWaitFrames)
                        throw Fail(IsoTpError.WaitFrameOverrun, IsoTpDirection.Send, "WAIT FC 최대 횟수 초과.");
                    continue;
                case FlowStatus.Overflow:
                    throw Fail(IsoTpError.FlowControlOverflow, IsoTpDirection.Send, "수신측 OVFLW 통지.");
                default:
                    throw Fail(IsoTpError.UnexpectedPdu, IsoTpDirection.Send, "알 수 없는 FC FlowStatus.");
            }
        }
    }

    // ===================== 수신 상태머신 =====================

    private async Task ReceiverLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // IDLE: 첫 프레임을 무기한 대기.
                IsoTpFrame first;
                try
                {
                    first = await _rxDataChannel.Reader.ReadAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }

                if (first.Type == IsoTpFrameType.SingleFrame)
                {
                    DeliverMessage(first.Data.ToArray());
                    continue;
                }

                if (first.Type == IsoTpFrameType.FirstFrame)
                {
                    await ReceiveMultiFrameAsync(first, token).ConfigureAwait(false);
                    continue;
                }

                // IDLE 상태에서의 CF/Unknown은 무시 (지나간 전송의 잔여 프레임 등).
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task ReceiveMultiFrameAsync(IsoTpFrame ff, CancellationToken token)
    {
        long total = ff.Length;

        if (total > Config.MaxMessageBytes || total < 0)
        {
            await SendRawAsync(IsoTpPci.EncodeFlowControl(FlowStatus.Overflow, 0, 0), token).ConfigureAwait(false);
            RaiseError(IsoTpError.BufferOverflow, IsoTpDirection.Receive,
                $"메시지 길이 {total} > MaxMessageBytes {Config.MaxMessageBytes}.");
            return;
        }

        var buffer = new byte[total];
        int received = CopyInto(buffer, 0, ff.Data.Span, total);
        RaiseProgress(IsoTpDirection.Receive, received, total);

        // CTS 송신.
        await SendRawAsync(IsoTpPci.EncodeFlowControl(FlowStatus.ContinueToSend, Config.BlockSize, Config.STmin),
            token).ConfigureAwait(false);

        byte expectedSn = 1;
        int blockCount = 0;

        while (received < total)
        {
            var cf = await ReadFrameWithTimeoutAsync(_rxDataChannel.Reader, Config.N_Cr_ms, token).ConfigureAwait(false);
            if (cf is null)
            {
                RaiseError(IsoTpError.TimeoutCr, IsoTpDirection.Receive, "CF 수신 타임아웃 (N_Cr).");
                return;
            }

            if (cf.Value.Type != IsoTpFrameType.ConsecutiveFrame)
            {
                RaiseError(IsoTpError.UnexpectedPdu, IsoTpDirection.Receive, "CF 기대 중 다른 PCI 수신.");
                return;
            }

            if (cf.Value.SequenceNumber != expectedSn)
            {
                RaiseError(IsoTpError.WrongSequenceNumber, IsoTpDirection.Receive,
                    $"SN 불일치: 기대 {expectedSn}, 수신 {cf.Value.SequenceNumber}.");
                return;
            }

            received += CopyInto(buffer, received, cf.Value.Data.Span, total);
            expectedSn = (byte)((expectedSn + 1) & 0x0F);
            blockCount++;
            RaiseProgress(IsoTpDirection.Receive, received, total);

            if (received >= total) break;

            // Block 완료 시 다음 CTS 송신.
            if (Config.BlockSize != 0 && blockCount == Config.BlockSize)
            {
                await SendRawAsync(
                    IsoTpPci.EncodeFlowControl(FlowStatus.ContinueToSend, Config.BlockSize, Config.STmin),
                    token).ConfigureAwait(false);
                blockCount = 0;
            }
        }

        DeliverMessage(buffer);
    }

    /// <summary>src 데이터를 buffer[offset..]에 복사하되 total을 넘지 않도록 자른다. 복사한 바이트 수 반환.</summary>
    private static int CopyInto(byte[] buffer, int offset, ReadOnlySpan<byte> src, long total)
    {
        int remaining = (int)(total - offset);
        int n = Math.Min(remaining, src.Length);
        src[..n].CopyTo(buffer.AsSpan(offset));
        return n;
    }

    // ===================== 공통 유틸 =====================

    /// <summary>PCI 바이트열을 CAN-FD 유효 길이로 패딩하여 한 프레임 송신.</summary>
    private async Task SendRawAsync(byte[] pci, CancellationToken ct)
    {
        int frameLen = CanFdLength.Ceil(pci.Length);
        byte[] data;
        if (frameLen == pci.Length)
        {
            data = pci;
        }
        else
        {
            data = new byte[frameLen];
            Array.Copy(pci, data, pci.Length);
            for (int i = pci.Length; i < frameLen; i++)
                data[i] = Config.PaddingByte;
        }

        var frame = new CanFdFrame(Config.TxCanId, data, Config.ExtendedId, fd: true, Config.BitRateSwitch);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(Config.N_A_ms);
        try
        {
            await _driver.SendFrameAsync(frame, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Fail(IsoTpError.TimeoutA, IsoTpDirection.Send, "CAN 프레임 송신 타임아웃 (N_As/N_Ar).");
        }
    }

    private static async Task<IsoTpFrame?> ReadFrameWithTimeoutAsync(
        ChannelReader<IsoTpFrame> reader, int timeoutMs, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeoutMs);
        try
        {
            return await reader.ReadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // timeout
        }
    }

    private void DeliverMessage(byte[] payload)
    {
        MessageReceived?.Invoke(this, new IsoTpMessageReceivedEventArgs
        {
            Payload = payload,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    private void RaiseProgress(IsoTpDirection dir, long transferred, long total)
    {
        Progress?.Invoke(this, new IsoTpProgressEventArgs
        {
            Direction = dir,
            BytesTransferred = transferred,
            TotalBytes = total
        });
    }

    private void RaiseError(IsoTpError error, IsoTpDirection dir, string message)
    {
        ErrorOccurred?.Invoke(this, new IsoTpErrorEventArgs
        {
            Error = error,
            Direction = dir,
            Message = message
        });
    }

    private IsoTpException Fail(IsoTpError error, IsoTpDirection dir, string message)
    {
        RaiseError(error, dir, message);
        return new IsoTpException(error, message);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private sealed class SendItem
    {
        public SendItem(ReadOnlyMemory<byte> payload, CancellationToken userToken)
        {
            Payload = payload;
            UserToken = userToken;
        }

        public ReadOnlyMemory<byte> Payload { get; }
        public CancellationToken UserToken { get; }
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
