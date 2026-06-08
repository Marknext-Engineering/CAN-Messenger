// =====================================================================
//  IsoTpPci — ISO-TP PCI(프로토콜 제어 정보) 인코딩/디코딩
//  개요   : SF(단일)/FF(첫)/CF(연속)/FC(흐름제어) 프레임의 바이트 포맷 처리.
//           CAN-FD 64byte 기준(SF≤62, FF 6byte escape로 32-bit 길이까지).
//  핵심   : EncodeSingleFrame/EncodeFirstFrame/EncodeConsecutiveFrame/EncodeFlowControl
//           Decode(수신 프레임 1개 파싱), DecodeSTmin(STmin 바이트→지연시간)
//  추후 개선:
//           - 확장 주소(Extended Addressing)/Mixed Addressing 미지원(Normal만)
//           - CAN 2.0(8byte) 폴백 미지원 — 본 프로젝트는 CAN-FD 전제
// =====================================================================
using System.Buffers.Binary;

namespace CanMessager.Core.Transport;

/// <summary>ISO-TP 프레임(PCI) 종류.</summary>
public enum IsoTpFrameType
{
    SingleFrame = 0x0,
    FirstFrame = 0x1,
    ConsecutiveFrame = 0x2,
    FlowControl = 0x3,
    Unknown = 0xFF
}

/// <summary>Flow Control 상태.</summary>
public enum FlowStatus
{
    ContinueToSend = 0x0,
    Wait = 0x1,
    Overflow = 0x2
}

/// <summary>파싱된 ISO-TP 프레임 한 개의 의미를 담는 값 타입.</summary>
public readonly struct IsoTpFrame
{
    public IsoTpFrameType Type { get; init; }

    /// <summary>SF/FF: 메시지 전체 길이. CF: 미사용(0).</summary>
    public long Length { get; init; }

    /// <summary>CF: Sequence Number (0..15).</summary>
    public int SequenceNumber { get; init; }

    /// <summary>FC: Flow Status.</summary>
    public FlowStatus FlowStatus { get; init; }

    /// <summary>FC: Block Size.</summary>
    public byte BlockSize { get; init; }

    /// <summary>FC: STmin.</summary>
    public byte STmin { get; init; }

    /// <summary>SF/FF/CF: 이 프레임이 운반하는 페이로드 데이터.</summary>
    public ReadOnlyMemory<byte> Data { get; init; }
}

/// <summary>
/// CAN-FD용 ISO-TP PCI 인코딩/디코딩.
/// docs/01_ISO-TP_Layer_Design.md 2장의 프레임 포맷을 구현한다.
/// </summary>
public static class IsoTpPci
{
    public const int MaxFramePayload = 64;

    // SF: 1-byte PCI는 데이터 1..7, 2-byte PCI(escape)는 데이터 최대 62.
    public const int SfMaxDataSmall = 7;
    public const int SfMaxData = MaxFramePayload - 2; // 62

    // FF: 2-byte PCI(len<=4095) 데이터 62, 6-byte PCI(escape) 데이터 58.
    public const int FfMaxDataSmall = MaxFramePayload - 2; // 62
    public const int FfMaxData = MaxFramePayload - 6;       // 58

    // CF: 1-byte PCI, 데이터 최대 63.
    public const int CfMaxData = MaxFramePayload - 1; // 63

    private const long FfSmallLimit = 4095;

    // ---- Single Frame ----

    /// <summary>SF 인코딩. payload 길이는 0..62 이어야 한다.</summary>
    public static byte[] EncodeSingleFrame(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > SfMaxData)
            throw new ArgumentException($"SF payload too large ({payload.Length} > {SfMaxData}).", nameof(payload));

        if (payload.Length <= SfMaxDataSmall)
        {
            // 1-byte PCI: [0x0 | SF_DL]
            var frame = new byte[1 + payload.Length];
            frame[0] = (byte)((int)IsoTpFrameType.SingleFrame << 4 | payload.Length);
            payload.CopyTo(frame.AsSpan(1));
            return frame;
        }
        else
        {
            // 2-byte PCI(escape): [0x00][SF_DL]
            var frame = new byte[2 + payload.Length];
            frame[0] = (byte)((int)IsoTpFrameType.SingleFrame << 4); // 상위 nibble 0, 하위 nibble 0
            frame[1] = (byte)payload.Length;
            payload.CopyTo(frame.AsSpan(2));
            return frame;
        }
    }

    // ---- First Frame ----

    /// <summary>FF 인코딩. 전체 메시지 길이와 페이로드를 받아 첫 프레임을 만들고, 소비한 바이트 수를 반환.</summary>
    public static byte[] EncodeFirstFrame(long totalLength, ReadOnlySpan<byte> payload, out int consumed)
    {
        if (totalLength <= FfSmallLimit)
        {
            // 2-byte PCI: [0x1 | len_hi][len_lo]
            consumed = Math.Min(FfMaxDataSmall, payload.Length);
            var frame = new byte[2 + consumed];
            frame[0] = (byte)((int)IsoTpFrameType.FirstFrame << 4 | (int)((totalLength >> 8) & 0x0F));
            frame[1] = (byte)(totalLength & 0xFF);
            payload[..consumed].CopyTo(frame.AsSpan(2));
            return frame;
        }
        else
        {
            // 6-byte PCI(escape): [0x10][0x00][len 32bit BE]
            consumed = Math.Min(FfMaxData, payload.Length);
            var frame = new byte[6 + consumed];
            frame[0] = (byte)((int)IsoTpFrameType.FirstFrame << 4); // 0x10
            frame[1] = 0x00;
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(2, 4), (uint)totalLength);
            payload[..consumed].CopyTo(frame.AsSpan(6));
            return frame;
        }
    }

    // ---- Consecutive Frame ----

    /// <summary>CF 인코딩. sn은 0..15. 마지막 프레임은 padToLength로 지정한 길이까지 패딩.</summary>
    public static byte[] EncodeConsecutiveFrame(ReadOnlySpan<byte> payload, int offset, byte sn,
        out int consumed, byte paddingByte, bool padToValidLength)
    {
        consumed = Math.Min(CfMaxData, payload.Length - offset);
        int dataLen = consumed;
        int frameLen = 1 + dataLen;

        if (padToValidLength)
            frameLen = Hal.CanFdLength.Ceil(frameLen);

        var frame = new byte[frameLen];
        frame[0] = (byte)((int)IsoTpFrameType.ConsecutiveFrame << 4 | (sn & 0x0F));
        payload.Slice(offset, dataLen).CopyTo(frame.AsSpan(1));

        for (int i = 1 + dataLen; i < frameLen; i++)
            frame[i] = paddingByte;

        return frame;
    }

    // ---- Flow Control ----

    public static byte[] EncodeFlowControl(FlowStatus fs, byte blockSize, byte stMin)
    {
        // 3-byte PCI. (패딩 없이 최소 길이로 전송)
        return new byte[]
        {
            (byte)((int)IsoTpFrameType.FlowControl << 4 | (int)fs & 0x0F),
            blockSize,
            stMin
        };
    }

    // ---- Decoding ----

    /// <summary>수신 프레임 1개를 파싱한다.</summary>
    public static IsoTpFrame Decode(ReadOnlyMemory<byte> raw)
    {
        var span = raw.Span;
        if (span.Length == 0)
            return new IsoTpFrame { Type = IsoTpFrameType.Unknown };

        var type = (IsoTpFrameType)(span[0] >> 4);
        switch (type)
        {
            case IsoTpFrameType.SingleFrame:
            {
                int sfDl = span[0] & 0x0F;
                if (sfDl != 0)
                {
                    // 1-byte PCI
                    return new IsoTpFrame
                    {
                        Type = type,
                        Length = sfDl,
                        Data = raw.Slice(1, Math.Min(sfDl, span.Length - 1))
                    };
                }
                // 2-byte PCI(escape)
                if (span.Length < 2) return new IsoTpFrame { Type = IsoTpFrameType.Unknown };
                int len = span[1];
                return new IsoTpFrame
                {
                    Type = type,
                    Length = len,
                    Data = raw.Slice(2, Math.Min(len, span.Length - 2))
                };
            }
            case IsoTpFrameType.FirstFrame:
            {
                int hi = span[0] & 0x0F;
                int lo = span.Length > 1 ? span[1] : 0;
                long len = (hi << 8) | lo;
                if (len != 0)
                {
                    // 2-byte PCI
                    return new IsoTpFrame
                    {
                        Type = type,
                        Length = len,
                        Data = raw.Slice(2)
                    };
                }
                // 6-byte PCI(escape)
                if (span.Length < 6) return new IsoTpFrame { Type = IsoTpFrameType.Unknown };
                long len32 = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(2, 4));
                return new IsoTpFrame
                {
                    Type = type,
                    Length = len32,
                    Data = raw.Slice(6)
                };
            }
            case IsoTpFrameType.ConsecutiveFrame:
            {
                return new IsoTpFrame
                {
                    Type = type,
                    SequenceNumber = span[0] & 0x0F,
                    Data = raw.Slice(1)
                };
            }
            case IsoTpFrameType.FlowControl:
            {
                return new IsoTpFrame
                {
                    Type = type,
                    FlowStatus = (FlowStatus)(span[0] & 0x0F),
                    BlockSize = span.Length > 1 ? span[1] : (byte)0,
                    STmin = span.Length > 2 ? span[2] : (byte)0
                };
            }
            default:
                return new IsoTpFrame { Type = IsoTpFrameType.Unknown };
        }
    }

    /// <summary>STmin 바이트를 실제 지연 시간으로 변환한다.</summary>
    public static TimeSpan DecodeSTmin(byte stMin)
    {
        if (stMin <= 0x7F)
            return TimeSpan.FromMilliseconds(stMin);          // 0..127 ms
        if (stMin >= 0xF1 && stMin <= 0xF9)
            return TimeSpan.FromMicroseconds((stMin - 0xF0) * 100); // 100..900 us
        return TimeSpan.Zero;                                  // reserved → 0 처리
    }
}
