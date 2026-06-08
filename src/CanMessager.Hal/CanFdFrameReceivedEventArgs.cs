namespace CanMessager.Hal;

/// <summary>드라이버가 CAN-FD 프레임을 수신했을 때 발생하는 이벤트 인자.</summary>
public sealed class CanFdFrameReceivedEventArgs : EventArgs
{
    public CanFdFrameReceivedEventArgs(CanFdFrame frame) => Frame = frame;

    /// <summary>수신된 프레임.</summary>
    public CanFdFrame Frame { get; }
}
