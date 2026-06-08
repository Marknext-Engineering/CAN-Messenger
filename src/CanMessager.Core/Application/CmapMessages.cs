namespace CanMessager.Core.Application;

/// <summary>모든 CMAP PDU의 공통 헤더 (docs/02 3장).</summary>
public readonly record struct CmapHeader(byte Version, CmapMsgType Type, CmapFlags Flags, uint TransferId);

public readonly record struct HelloMessage(
    byte MaxVersion, ushort MaxIsoTpBytes, uint PreferredChunk, ushort PreferredWindow, byte CapFlags);

public readonly record struct HelloAckMessage(
    byte AgreedVersion, uint AgreedChunk, ushort AgreedWindow, byte CapFlags);

public readonly record struct MetaMessage(
    uint ChunkSize, long TotalSize, uint TotalChunks, byte[] FileSha256, ushort WindowSize, string FileName);

public readonly record struct MetaAckMessage(
    MetaAckStatus Status, uint ResumeChunk, CmapReason RejectReason);

/// <summary>DATA 본문. Data는 원본 메시지 버퍼를 가리키는 슬라이스일 수 있다.</summary>
public readonly record struct DataMessage(
    uint ChunkIndex, uint Crc32, bool HasCrc, ReadOnlyMemory<byte> Data);

/// <summary>ACK/NAK 본문. 비트맵 의미는 MsgType에 따른다 (docs/02 4.6).</summary>
public readonly record struct AckMessage(
    uint BlockBaseIndex, ushort BitCount, byte[] Bitmap);

public readonly record struct FinMessage(uint TotalChunks);

public readonly record struct FinAckMessage(FinResult Result, uint MissingCount);

public readonly record struct AbortErrorMessage(CmapReason Code, string Message);
