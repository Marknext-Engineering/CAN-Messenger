namespace CanMessager.Hal.Fake;

/// <summary>
/// 테스트/데모용 가상 CAN-FD 드라이버. <see cref="FakeCanBus"/>에 연결되어
/// 실제 하드웨어 없이 프레임 송수신을 시뮬레이션한다.
/// </summary>
public sealed class FakeCanDriver : ICanDriver
{
    private readonly FakeCanBus _bus;
    private volatile bool _open;

    public FakeCanDriver(FakeCanBus bus) => _bus = bus ?? throw new ArgumentNullException(nameof(bus));

    public bool IsOpen => _open;

    public event EventHandler<CanFdFrameReceivedEventArgs>? FrameReceived;

    public Task OpenAsync(CanDriverConfig config, CancellationToken ct = default)
    {
        if (_open) return Task.CompletedTask;
        _bus.Attach(this);
        _open = true;
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        if (!_open) return Task.CompletedTask;
        _bus.Detach(this);
        _open = false;
        return Task.CompletedTask;
    }

    public Task SendFrameAsync(CanFdFrame frame, CancellationToken ct = default)
    {
        if (!_open) throw new InvalidOperationException("Driver is not open.");
        ct.ThrowIfCancellationRequested();
        return _bus.BroadcastAsync(this, frame, ct);
    }

    /// <summary>버스가 이 드라이버로 프레임을 전달할 때 호출 (내부용).</summary>
    internal void DeliverFrame(CanFdFrame frame)
    {
        if (!_open) return;
        FrameReceived?.Invoke(this, new CanFdFrameReceivedEventArgs(frame));
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
    }
}
