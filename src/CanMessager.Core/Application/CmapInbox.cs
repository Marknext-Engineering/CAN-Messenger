// =====================================================================
//  CmapInbox — ISO-TP 수신 메시지를 CMAP PDU로 큐잉/대기
//  개요   : IIsoTpChannel.MessageReceived → 헤더 파싱 → 내부 큐 적재.
//           ReceiveAsync(타임아웃)/ReceiveTypeAsync(특정 타입까지 대기)로 소비.
//  추후 개선:
//           - 타입 불일치 PDU는 버림 → ABORT/ERROR 비동기 처리 콜백 분리 여지
//           - Timeout.Infinite 대기는 취소 토큰에만 의존(HELLO 대기 등)
// =====================================================================
using System.Threading.Channels;
using CanMessager.Core.Transport;

namespace CanMessager.Core.Application;

/// <summary>수신된 CMAP PDU 한 개 (헤더 + 원본 버퍼).</summary>
public readonly record struct CmapEnvelope(CmapHeader Header, ReadOnlyMemory<byte> Raw)
{
    public ReadOnlyMemory<byte> Body => CmapCodec.Body(Raw);
}

/// <summary>
/// <see cref="IIsoTpChannel"/>에서 올라오는 메시지를 디코딩해 큐잉하고,
/// 타임아웃 기반으로 한 개씩 꺼내쓸 수 있게 하는 헬퍼.
/// </summary>
public sealed class CmapInbox : IDisposable
{
    private readonly IIsoTpChannel _channel;
    private readonly Channel<CmapEnvelope> _queue =
        Channel.CreateUnbounded<CmapEnvelope>(new UnboundedChannelOptions { SingleReader = true });

    public CmapInbox(IIsoTpChannel channel)
    {
        _channel = channel;
        _channel.MessageReceived += OnMessage;
    }

    private void OnMessage(object? sender, IsoTpMessageReceivedEventArgs e)
    {
        // ISO-TP 페이로드를 복사해 보관 (원본 버퍼 재사용 방지).
        var raw = e.Payload.ToArray();
        if (CmapCodec.TryReadHeader(raw, out var header))
            _queue.Writer.TryWrite(new CmapEnvelope(header, raw));
    }

    /// <summary>다음 PDU를 timeout 내에 받는다. 타임아웃이면 null. <see cref="Timeout.Infinite"/>면 무기한 대기.</summary>
    public async Task<CmapEnvelope?> ReceiveAsync(int timeoutMs, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs != Timeout.Infinite)
            linked.CancelAfter(timeoutMs);
        try
        {
            return await _queue.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>특정 타입의 PDU를 받을 때까지 다른 타입은 버리며 기다린다. 타임아웃이면 null.</summary>
    public async Task<CmapEnvelope?> ReceiveTypeAsync(CmapMsgType type, int timeoutMs, CancellationToken ct)
    {
        bool infinite = timeoutMs == Timeout.Infinite;
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            int remaining;
            if (infinite)
            {
                remaining = Timeout.Infinite;
            }
            else
            {
                remaining = (int)(deadline - Environment.TickCount64);
                if (remaining <= 0) return null;
            }

            var env = await ReceiveAsync(remaining, ct).ConfigureAwait(false);
            if (env is null) return null;
            if (env.Value.Header.Type == type) return env;
            // 그 외 타입(ABORT/ERROR 등)은 호출측이 별도 처리하도록 무시.
        }
    }

    public void Dispose() => _channel.MessageReceived -= OnMessage;
}
