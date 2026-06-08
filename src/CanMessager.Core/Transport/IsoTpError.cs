namespace CanMessager.Core.Transport;

/// <summary>ISO-TP 네트워크 계층 에러 종류 (docs/01 7장).</summary>
public enum IsoTpError
{
    /// <summary>CAN 프레임 송신 타임아웃 (N_As / N_Ar).</summary>
    TimeoutA,

    /// <summary>FC 수신 타임아웃 (N_Bs).</summary>
    TimeoutBs,

    /// <summary>CF 수신 타임아웃 (N_Cr).</summary>
    TimeoutCr,

    /// <summary>CF Sequence Number 불일치.</summary>
    WrongSequenceNumber,

    /// <summary>수신 버퍼 초과 (메시지 길이 > MaxMessageBytes).</summary>
    BufferOverflow,

    /// <summary>진행 중 예상치 못한 PCI 수신.</summary>
    UnexpectedPdu,

    /// <summary>WAIT FC 최대 횟수 초과.</summary>
    WaitFrameOverrun,

    /// <summary>수신측이 OVFLW FC를 보내 송신이 중단됨.</summary>
    FlowControlOverflow,

    /// <summary>그 외 일반 에러.</summary>
    General
}

/// <summary>ISO-TP 처리 중 발생하는 예외.</summary>
public sealed class IsoTpException : Exception
{
    public IsoTpException(IsoTpError error, string? message = null, Exception? inner = null)
        : base(message ?? error.ToString(), inner)
    {
        Error = error;
    }

    public IsoTpError Error { get; }
}
