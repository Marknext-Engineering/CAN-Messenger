// =====================================================================
//  CmapCodec — CMAP(앱 프로토콜) PDU 인코딩/디코딩
//  개요   : 공통 10-byte 헤더(Magic/Version/MsgType/Flags/TransferId) + 메시지별 본문.
//           모든 정수는 Big-Endian, 파일명은 UTF-8. docs/02 참조.
//  핵심   : Encode*/Decode* (Hello/HelloAck/Meta/MetaAck/Data/Ack/Fin/FinAck/Abort)
//           TryReadHeader(헤더 검증), Body(본문 슬라이스)
//  추후 개선:
//           - 버전 협상은 단순 수락 위주 → 하위호환 정책 정교화 여지
//           - DATA의 CRC32는 옵션 — 항상 ISO-TP/CAN CRC 위에 추가 보호
// =====================================================================
using System.Buffers.Binary;
using System.Text;

namespace CanMessager.Core.Application;

/// <summary>
/// CMAP PDU 인코딩/디코딩 (Big-Endian). docs/02 3~4장.
/// 모든 Encode*는 [공통 10-byte 헤더 + 본문] 전체 바이트열을 반환한다.
/// </summary>
public static class CmapCodec
{
    // ---- 헤더 ----

    private static void WriteHeader(Span<byte> dst, CmapMsgType type, CmapFlags flags, uint transferId)
    {
        dst[0] = CmapConstants.Magic0;
        dst[1] = CmapConstants.Magic1;
        dst[2] = CmapConstants.Version;
        dst[3] = (byte)type;
        dst[4] = (byte)flags;
        dst[5] = 0x00; // Reserved
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(6, 4), transferId);
    }

    /// <summary>공통 헤더만 파싱. 유효하지 않으면 false.</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> raw, out CmapHeader header)
    {
        header = default;
        if (raw.Length < CmapConstants.HeaderLength) return false;
        if (raw[0] != CmapConstants.Magic0 || raw[1] != CmapConstants.Magic1) return false;

        header = new CmapHeader(
            raw[2],
            (CmapMsgType)raw[3],
            (CmapFlags)raw[4],
            BinaryPrimitives.ReadUInt32BigEndian(raw.Slice(6, 4)));
        return true;
    }

    /// <summary>헤더 이후 본문 슬라이스.</summary>
    public static ReadOnlyMemory<byte> Body(ReadOnlyMemory<byte> raw) => raw.Slice(CmapConstants.HeaderLength);

    // ---- HELLO ----

    public static byte[] EncodeHello(uint transferId, in HelloMessage m)
    {
        var buf = new byte[CmapConstants.HeaderLength + 10];
        WriteHeader(buf, CmapMsgType.Hello, CmapFlags.None, transferId);
        var b = buf.AsSpan(CmapConstants.HeaderLength);
        b[0] = m.MaxVersion;
        BinaryPrimitives.WriteUInt16BigEndian(b.Slice(1, 2), m.MaxIsoTpBytes);
        BinaryPrimitives.WriteUInt32BigEndian(b.Slice(3, 4), m.PreferredChunk);
        BinaryPrimitives.WriteUInt16BigEndian(b.Slice(7, 2), m.PreferredWindow);
        b[9] = m.CapFlags;
        return buf;
    }

    public static HelloMessage DecodeHello(ReadOnlySpan<byte> body) => new(
        body[0],
        BinaryPrimitives.ReadUInt16BigEndian(body.Slice(1, 2)),
        BinaryPrimitives.ReadUInt32BigEndian(body.Slice(3, 4)),
        BinaryPrimitives.ReadUInt16BigEndian(body.Slice(7, 2)),
        body[9]);

    // ---- HELLO_ACK ----

    public static byte[] EncodeHelloAck(uint transferId, in HelloAckMessage m)
    {
        var buf = new byte[CmapConstants.HeaderLength + 8];
        WriteHeader(buf, CmapMsgType.HelloAck, CmapFlags.None, transferId);
        var b = buf.AsSpan(CmapConstants.HeaderLength);
        b[0] = m.AgreedVersion;
        BinaryPrimitives.WriteUInt32BigEndian(b.Slice(1, 4), m.AgreedChunk);
        BinaryPrimitives.WriteUInt16BigEndian(b.Slice(5, 2), m.AgreedWindow);
        b[7] = m.CapFlags;
        return buf;
    }

    public static HelloAckMessage DecodeHelloAck(ReadOnlySpan<byte> body) => new(
        body[0],
        BinaryPrimitives.ReadUInt32BigEndian(body.Slice(1, 4)),
        BinaryPrimitives.ReadUInt16BigEndian(body.Slice(5, 2)),
        body[7]);

    // ---- META ----

    public static byte[] EncodeMeta(uint transferId, in MetaMessage m)
    {
        var nameBytes = Encoding.UTF8.GetBytes(m.FileName);
        var buf = new byte[CmapConstants.HeaderLength + 52 + nameBytes.Length];
        WriteHeader(buf, CmapMsgType.Meta, CmapFlags.None, transferId);
        var b = buf.AsSpan(CmapConstants.HeaderLength);
        BinaryPrimitives.WriteUInt32BigEndian(b.Slice(0, 4), m.ChunkSize);
        BinaryPrimitives.WriteInt64BigEndian(b.Slice(4, 8), m.TotalSize);
        BinaryPrimitives.WriteUInt32BigEndian(b.Slice(12, 4), m.TotalChunks);
        m.FileSha256.AsSpan(0, 32).CopyTo(b.Slice(16, 32));
        BinaryPrimitives.WriteUInt16BigEndian(b.Slice(48, 2), m.WindowSize);
        BinaryPrimitives.WriteUInt16BigEndian(b.Slice(50, 2), (ushort)nameBytes.Length);
        nameBytes.CopyTo(b.Slice(52));
        return buf;
    }

    public static MetaMessage DecodeMeta(ReadOnlySpan<byte> body)
    {
        uint chunkSize = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(0, 4));
        long totalSize = BinaryPrimitives.ReadInt64BigEndian(body.Slice(4, 8));
        uint totalChunks = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(12, 4));
        var sha = body.Slice(16, 32).ToArray();
        ushort window = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(48, 2));
        ushort nameLen = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(50, 2));
        string name = Encoding.UTF8.GetString(body.Slice(52, nameLen));
        return new MetaMessage(chunkSize, totalSize, totalChunks, sha, window, name);
    }

    // ---- META_ACK ----

    public static byte[] EncodeMetaAck(uint transferId, in MetaAckMessage m)
    {
        var buf = new byte[CmapConstants.HeaderLength + 6];
        WriteHeader(buf, CmapMsgType.MetaAck, CmapFlags.None, transferId);
        var b = buf.AsSpan(CmapConstants.HeaderLength);
        b[0] = (byte)m.Status;
        BinaryPrimitives.WriteUInt32BigEndian(b.Slice(1, 4), m.ResumeChunk);
        b[5] = (byte)m.RejectReason;
        return buf;
    }

    public static MetaAckMessage DecodeMetaAck(ReadOnlySpan<byte> body) => new(
        (MetaAckStatus)body[0],
        BinaryPrimitives.ReadUInt32BigEndian(body.Slice(1, 4)),
        (CmapReason)body[5]);

    // ---- DATA ----

    public static byte[] EncodeData(uint transferId, uint chunkIndex, ReadOnlySpan<byte> data,
        bool includeCrc, bool ackRequest)
    {
        var flags = CmapFlags.None;
        if (includeCrc) flags |= CmapFlags.ChunkCrc;
        if (ackRequest) flags |= CmapFlags.AckRequest;

        int crcLen = includeCrc ? 4 : 0;
        var buf = new byte[CmapConstants.HeaderLength + 8 + crcLen + data.Length];
        WriteHeader(buf, CmapMsgType.Data, flags, transferId);
        var b = buf.AsSpan(CmapConstants.HeaderLength);
        BinaryPrimitives.WriteUInt32BigEndian(b.Slice(0, 4), chunkIndex);
        BinaryPrimitives.WriteUInt32BigEndian(b.Slice(4, 4), (uint)data.Length);
        int off = 8;
        if (includeCrc)
        {
            BinaryPrimitives.WriteUInt32BigEndian(b.Slice(8, 4), Crc32.Compute(data));
            off = 12;
        }
        data.CopyTo(b.Slice(off));
        return buf;
    }

    public static DataMessage DecodeData(CmapFlags flags, ReadOnlyMemory<byte> body)
    {
        var span = body.Span;
        uint chunkIndex = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(0, 4));
        uint chunkLen = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4, 4));
        bool hasCrc = flags.HasFlag(CmapFlags.ChunkCrc);
        uint crc = 0;
        int off = 8;
        if (hasCrc)
        {
            crc = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(8, 4));
            off = 12;
        }
        return new DataMessage(chunkIndex, crc, hasCrc, body.Slice(off, (int)chunkLen));
    }

    // ---- ACK / NAK ----

    public static byte[] EncodeAck(uint transferId, bool isNak, uint blockBaseIndex, ushort bitCount, byte[] bitmap)
    {
        var buf = new byte[CmapConstants.HeaderLength + 6 + bitmap.Length];
        WriteHeader(buf, isNak ? CmapMsgType.Nak : CmapMsgType.Ack, CmapFlags.None, transferId);
        var b = buf.AsSpan(CmapConstants.HeaderLength);
        BinaryPrimitives.WriteUInt32BigEndian(b.Slice(0, 4), blockBaseIndex);
        BinaryPrimitives.WriteUInt16BigEndian(b.Slice(4, 2), bitCount);
        bitmap.CopyTo(b.Slice(6));
        return buf;
    }

    public static AckMessage DecodeAck(ReadOnlySpan<byte> body)
    {
        uint baseIndex = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(0, 4));
        ushort bitCount = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(4, 2));
        int k = (bitCount + 7) / 8;
        var bitmap = body.Slice(6, k).ToArray();
        return new AckMessage(baseIndex, bitCount, bitmap);
    }

    // ---- FIN ----

    public static byte[] EncodeFin(uint transferId, uint totalChunks)
    {
        var buf = new byte[CmapConstants.HeaderLength + 4];
        WriteHeader(buf, CmapMsgType.Fin, CmapFlags.None, transferId);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(CmapConstants.HeaderLength, 4), totalChunks);
        return buf;
    }

    public static FinMessage DecodeFin(ReadOnlySpan<byte> body) =>
        new(BinaryPrimitives.ReadUInt32BigEndian(body.Slice(0, 4)));

    // ---- FIN_ACK ----

    public static byte[] EncodeFinAck(uint transferId, in FinAckMessage m)
    {
        var buf = new byte[CmapConstants.HeaderLength + 5];
        WriteHeader(buf, CmapMsgType.FinAck, CmapFlags.None, transferId);
        var b = buf.AsSpan(CmapConstants.HeaderLength);
        b[0] = (byte)m.Result;
        BinaryPrimitives.WriteUInt32BigEndian(b.Slice(1, 4), m.MissingCount);
        return buf;
    }

    public static FinAckMessage DecodeFinAck(ReadOnlySpan<byte> body) =>
        new((FinResult)body[0], BinaryPrimitives.ReadUInt32BigEndian(body.Slice(1, 4)));

    // ---- ABORT / ERROR ----

    public static byte[] EncodeAbortOrError(uint transferId, bool isError, CmapReason code, string message)
    {
        var msgBytes = Encoding.UTF8.GetBytes(message ?? string.Empty);
        var buf = new byte[CmapConstants.HeaderLength + 3 + msgBytes.Length];
        WriteHeader(buf, isError ? CmapMsgType.Error : CmapMsgType.Abort, CmapFlags.None, transferId);
        var b = buf.AsSpan(CmapConstants.HeaderLength);
        b[0] = (byte)code;
        BinaryPrimitives.WriteUInt16BigEndian(b.Slice(1, 2), (ushort)msgBytes.Length);
        msgBytes.CopyTo(b.Slice(3));
        return buf;
    }

    public static AbortErrorMessage DecodeAbortOrError(ReadOnlySpan<byte> body)
    {
        var code = (CmapReason)body[0];
        ushort len = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(1, 2));
        string msg = len > 0 ? Encoding.UTF8.GetString(body.Slice(3, len)) : string.Empty;
        return new AbortErrorMessage(code, msg);
    }
}
