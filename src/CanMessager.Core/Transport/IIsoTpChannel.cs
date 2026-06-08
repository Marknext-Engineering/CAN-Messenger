namespace CanMessager.Core.Transport;

/// <summary>
/// ISO-TP Transport Layer 채널. 상위 애플리케이션은 이 인터페이스를 통해
/// 가변 길이 메시지를 신뢰성 있게 송수신한다 (CAN-FD 64byte 프레임으로 세그멘테이션).
/// </summary>
public interface IIsoTpChannel : IAsyncDisposable
{
    IsoTpConfig Config { get; }

    /// <summary>채널을 시작한다 (수신 처리 시작).</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>채널을 정지한다.</summary>
    Task StopAsync();

    /// <summary>하나의 ISO-TP 메시지를 전송한다 (완료될 때까지 대기).</summary>
    Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default);

    /// <summary>완성된 메시지를 수신했을 때 발생.</summary>
    event EventHandler<IsoTpMessageReceivedEventArgs>? MessageReceived;

    /// <summary>에러 발생 시.</summary>
    event EventHandler<IsoTpErrorEventArgs>? ErrorOccurred;

    /// <summary>송/수신 진행 상황.</summary>
    event EventHandler<IsoTpProgressEventArgs>? Progress;
}
