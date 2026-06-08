namespace CanMessager.Core.Transport;

/// <summary>완성된 ISO-TP 메시지를 수신했을 때 발생.</summary>
public sealed class IsoTpMessageReceivedEventArgs : EventArgs
{
    public required ReadOnlyMemory<byte> Payload { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>ISO-TP 처리 중 에러가 발생했을 때.</summary>
public sealed class IsoTpErrorEventArgs : EventArgs
{
    public required IsoTpError Error { get; init; }
    public required IsoTpDirection Direction { get; init; }
    public string? Message { get; init; }
}

/// <summary>송/수신 진행 상황 통지.</summary>
public sealed class IsoTpProgressEventArgs : EventArgs
{
    public required IsoTpDirection Direction { get; init; }
    public required long BytesTransferred { get; init; }
    public required long TotalBytes { get; init; }
}

/// <summary>전송 방향.</summary>
public enum IsoTpDirection
{
    Send,
    Receive
}
