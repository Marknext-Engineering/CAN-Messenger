namespace CanMessager.Hal;

/// <summary>
/// CAN 인터페이스 하드웨어 추상화.
/// Vector(XL Driver), PEAK(PCAN-Basic), 그리고 테스트용 Fake 드라이버가 이 인터페이스를 구현한다.
/// 상위 ISO-TP 레이어는 구체 하드웨어를 알지 못한 채 이 인터페이스만 사용한다.
/// </summary>
public interface ICanDriver : IAsyncDisposable
{
    /// <summary>채널이 열려 있는지 여부.</summary>
    bool IsOpen { get; }

    /// <summary>드라이버 채널을 연다 (하드웨어 초기화, 비트레이트 설정).</summary>
    Task OpenAsync(CanDriverConfig config, CancellationToken ct = default);

    /// <summary>드라이버 채널을 닫는다.</summary>
    Task CloseAsync();

    /// <summary>단일 CAN-FD 프레임을 송신한다.</summary>
    Task SendFrameAsync(CanFdFrame frame, CancellationToken ct = default);

    /// <summary>프레임 수신 이벤트. 드라이버 내부 수신 스레드에서 발생할 수 있다.</summary>
    event EventHandler<CanFdFrameReceivedEventArgs>? FrameReceived;
}
