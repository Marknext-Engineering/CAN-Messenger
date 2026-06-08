namespace CanMessager.Core.Application;

/// <summary>CMAP(CAN Messager Application Protocol) 메시지 종류. docs/02 4장.</summary>
public enum CmapMsgType : byte
{
    Hello = 0x01,
    HelloAck = 0x02,
    Meta = 0x10,
    MetaAck = 0x11,
    Data = 0x20,
    Ack = 0x30,
    Nak = 0x31,
    Fin = 0x40,
    FinAck = 0x41,
    Abort = 0x7E,
    Error = 0x7F
}

/// <summary>공통 헤더 Flags 비트. docs/02 3.1 (ACK_REQUEST는 구현에서 추가).</summary>
[Flags]
public enum CmapFlags : byte
{
    None = 0,
    ChunkCrc = 1 << 0,
    Resume = 1 << 1,
    Compressed = 1 << 2,
    /// <summary>이 DATA가 블록의 마지막 → 수신측은 즉시 ACK 송신.</summary>
    AckRequest = 1 << 3
}

/// <summary>META_ACK 상태.</summary>
public enum MetaAckStatus : byte
{
    Accept = 0x00,
    Reject = 0x01,
    ResumeAccept = 0x02
}

/// <summary>FIN_ACK 결과.</summary>
public enum FinResult : byte
{
    Success = 0x00,
    HashMismatch = 0x01,
    Incomplete = 0x02
}

/// <summary>사유/거절/에러 코드. docs/02 5.4.</summary>
public enum CmapReason : byte
{
    None = 0x00,
    UnsupportedVersion = 0x01,
    FileTooLarge = 0x02,
    Busy = 0x03,
    UserRejected = 0x04,
    HashMismatch = 0x05,
    ChunkCrcError = 0x06,
    Timeout = 0x07,
    Canceled = 0x08,
    ProtocolError = 0x09
}

/// <summary>프로토콜 상수.</summary>
public static class CmapConstants
{
    public const byte Magic0 = 0x43; // 'C'
    public const byte Magic1 = 0x4D; // 'M'
    public const byte Version = 0x01;

    /// <summary>공통 헤더 길이 (byte).</summary>
    public const int HeaderLength = 10;
}
