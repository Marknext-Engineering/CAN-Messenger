namespace CanMessager.Hal.Fake;

/// <summary>
/// 테스트용 가상 CAN 버스. 여기에 연결된 모든 <see cref="FakeCanDriver"/>는
/// 한 드라이버가 송신한 프레임을 (자기 자신을 제외한) 나머지 드라이버들이 수신한다.
/// 실제 하드웨어 없이 ISO-TP 송수신을 양방향으로 검증하는 데 사용한다.
/// </summary>
public sealed class FakeCanBus
{
    private readonly List<FakeCanDriver> _drivers = new();
    private readonly Lock _gate = new();

    /// <summary>전송 지연 시뮬레이션 (프레임당). 기본 0.</summary>
    public TimeSpan FrameDelay { get; set; } = TimeSpan.Zero;

    internal void Attach(FakeCanDriver driver)
    {
        lock (_gate) _drivers.Add(driver);
    }

    internal void Detach(FakeCanDriver driver)
    {
        lock (_gate) _drivers.Remove(driver);
    }

    /// <summary><paramref name="sender"/>가 보낸 프레임을 나머지 드라이버에게 전달한다.</summary>
    internal async Task BroadcastAsync(FakeCanDriver sender, CanFdFrame frame, CancellationToken ct)
    {
        if (FrameDelay > TimeSpan.Zero)
            await Task.Delay(FrameDelay, ct).ConfigureAwait(false);

        FakeCanDriver[] targets;
        lock (_gate) targets = _drivers.Where(d => d != sender).ToArray();

        // 수신 측에는 타임스탬프를 채워서 전달.
        var stamped = frame with { Timestamp = DateTimeOffset.UtcNow };
        foreach (var target in targets)
            target.DeliverFrame(stamped);
    }
}
