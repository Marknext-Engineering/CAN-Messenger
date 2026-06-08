// =====================================================================
//  TracingCanDriver — ICanDriver 데코레이터(송/수신 프레임 트레이스 노출)
//  개요   : 실드라이버를 감싸 Enabled=true일 때만 FrameTraced 이벤트 발생.
//           UI의 "CAN 통신 트레이스" 체크박스로 런타임 on/off.
//  추후 개선: 트레이스를 .asc/.blf 등 표준 로그 파일로 저장하는 기능 추가 여지.
// =====================================================================
namespace CanMessager.Hal;

/// <summary>CAN 프레임 트레이스 방향.</summary>
public enum CanTraceDirection { Tx, Rx }

/// <summary>프레임 트레이스 이벤트 인자.</summary>
public sealed class CanTraceEventArgs : EventArgs
{
    public required CanTraceDirection Direction { get; init; }
    public required CanFdFrame Frame { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// 임의의 <see cref="ICanDriver"/>를 감싸 송/수신 프레임을 트레이스로 노출하는 데코레이터.
/// <see cref="Enabled"/>가 true일 때만 <see cref="FrameTraced"/>를 발생시킨다 (런타임 토글 가능).
/// </summary>
public sealed class TracingCanDriver : ICanDriver
{
    private readonly ICanDriver _inner;

    public TracingCanDriver(ICanDriver inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _inner.FrameReceived += OnInnerFrameReceived;
    }

    /// <summary>트레이스 활성화 여부.</summary>
    public bool Enabled { get; set; }

    /// <summary>프레임 송/수신 시 발생 (Enabled일 때만).</summary>
    public event EventHandler<CanTraceEventArgs>? FrameTraced;

    public bool IsOpen => _inner.IsOpen;

    public event EventHandler<CanFdFrameReceivedEventArgs>? FrameReceived;

    public Task OpenAsync(CanDriverConfig config, CancellationToken ct = default)
        => _inner.OpenAsync(config, ct);

    public Task CloseAsync() => _inner.CloseAsync();

    public Task SendFrameAsync(CanFdFrame frame, CancellationToken ct = default)
    {
        if (Enabled) Trace(CanTraceDirection.Tx, frame);
        return _inner.SendFrameAsync(frame, ct);
    }

    private void OnInnerFrameReceived(object? sender, CanFdFrameReceivedEventArgs e)
    {
        if (Enabled) Trace(CanTraceDirection.Rx, e.Frame);
        FrameReceived?.Invoke(this, e);
    }

    private void Trace(CanTraceDirection dir, CanFdFrame frame)
        => FrameTraced?.Invoke(this, new CanTraceEventArgs
        {
            Direction = dir,
            Frame = frame,
            Timestamp = DateTimeOffset.Now
        });

    public async ValueTask DisposeAsync()
    {
        _inner.FrameReceived -= OnInnerFrameReceived;
        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}
