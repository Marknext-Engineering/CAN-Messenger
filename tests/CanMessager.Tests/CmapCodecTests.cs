using CanMessager.Core.Application;

namespace CanMessager.Tests;

public class CmapCodecTests
{
    [Fact]
    public void Header_RoundTrips()
    {
        var raw = CmapCodec.EncodeFin(0xDEADBEEF, 123);
        Assert.True(CmapCodec.TryReadHeader(raw, out var h));
        Assert.Equal(CmapMsgType.Fin, h.Type);
        Assert.Equal(0xDEADBEEFu, h.TransferId);
        Assert.Equal(CmapConstants.Version, h.Version);
    }

    [Fact]
    public void Header_RejectsBadMagic()
    {
        var raw = CmapCodec.EncodeFin(1, 1);
        raw[0] = 0x00;
        Assert.False(CmapCodec.TryReadHeader(raw, out _));
    }

    [Fact]
    public void Hello_RoundTrips()
    {
        var m = new HelloMessage(1, 65535, 4096, 32, 0x01);
        var raw = CmapCodec.EncodeHello(7, m);
        CmapCodec.TryReadHeader(raw, out _);
        var d = CmapCodec.DecodeHello(CmapCodec.Body(raw).Span);
        Assert.Equal(m, d);
    }

    [Fact]
    public void HelloAck_RoundTrips()
    {
        var m = new HelloAckMessage(1, 4096, 16, 0x01);
        var raw = CmapCodec.EncodeHelloAck(7, m);
        var d = CmapCodec.DecodeHelloAck(CmapCodec.Body(raw).Span);
        Assert.Equal(m, d);
    }

    [Fact]
    public void Meta_RoundTrips()
    {
        var sha = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var m = new MetaMessage(4096, 123456789, 30151, sha, 32, "테스트 파일.zip");
        var raw = CmapCodec.EncodeMeta(42, m);
        var d = CmapCodec.DecodeMeta(CmapCodec.Body(raw).Span);

        Assert.Equal(m.ChunkSize, d.ChunkSize);
        Assert.Equal(m.TotalSize, d.TotalSize);
        Assert.Equal(m.TotalChunks, d.TotalChunks);
        Assert.Equal(m.WindowSize, d.WindowSize);
        Assert.Equal(m.FileName, d.FileName);
        Assert.Equal(sha, d.FileSha256);
    }

    [Fact]
    public void MetaAck_RoundTrips()
    {
        var m = new MetaAckMessage(MetaAckStatus.ResumeAccept, 100, CmapReason.None);
        var raw = CmapCodec.EncodeMetaAck(1, m);
        var d = CmapCodec.DecodeMetaAck(CmapCodec.Body(raw).Span);
        Assert.Equal(m, d);
    }

    [Fact]
    public void Data_WithCrc_RoundTrips()
    {
        var payload = Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray();
        var raw = CmapCodec.EncodeData(99, chunkIndex: 5, payload, includeCrc: true, ackRequest: true);

        CmapCodec.TryReadHeader(raw, out var h);
        Assert.True(h.Flags.HasFlag(CmapFlags.ChunkCrc));
        Assert.True(h.Flags.HasFlag(CmapFlags.AckRequest));

        var d = CmapCodec.DecodeData(h.Flags, CmapCodec.Body(raw));
        Assert.Equal(5u, d.ChunkIndex);
        Assert.True(d.HasCrc);
        Assert.Equal(Crc32.Compute(payload), d.Crc32);
        Assert.Equal(payload, d.Data.ToArray());
    }

    [Fact]
    public void Data_NoCrc_RoundTrips()
    {
        var payload = new byte[] { 9, 8, 7, 6 };
        var raw = CmapCodec.EncodeData(1, 0, payload, includeCrc: false, ackRequest: false);
        CmapCodec.TryReadHeader(raw, out var h);
        var d = CmapCodec.DecodeData(h.Flags, CmapCodec.Body(raw));
        Assert.False(d.HasCrc);
        Assert.Equal(payload, d.Data.ToArray());
    }

    [Fact]
    public void Ack_Bitmap_RoundTrips()
    {
        var bitmap = new byte[] { 0b1010_1010, 0b0000_0011 };
        var raw = CmapCodec.EncodeAck(1, isNak: false, blockBaseIndex: 64, bitCount: 10, bitmap);
        var d = CmapCodec.DecodeAck(CmapCodec.Body(raw).Span);
        Assert.Equal(64u, d.BlockBaseIndex);
        Assert.Equal(10, d.BitCount);
        Assert.Equal(bitmap, d.Bitmap);
    }

    [Fact]
    public void FinAck_RoundTrips()
    {
        var m = new FinAckMessage(FinResult.HashMismatch, 3);
        var raw = CmapCodec.EncodeFinAck(1, m);
        var d = CmapCodec.DecodeFinAck(CmapCodec.Body(raw).Span);
        Assert.Equal(m, d);
    }

    [Fact]
    public void AbortError_RoundTrips()
    {
        var raw = CmapCodec.EncodeAbortOrError(1, isError: true, CmapReason.ProtocolError, "헤더 오류");
        CmapCodec.TryReadHeader(raw, out var h);
        Assert.Equal(CmapMsgType.Error, h.Type);
        var d = CmapCodec.DecodeAbortOrError(CmapCodec.Body(raw).Span);
        Assert.Equal(CmapReason.ProtocolError, d.Code);
        Assert.Equal("헤더 오류", d.Message);
    }

    [Fact]
    public void Crc32_KnownVector()
    {
        // "123456789" → 0xCBF43926 (CRC-32/ISO-HDLC 표준 검증 벡터)
        var data = System.Text.Encoding.ASCII.GetBytes("123456789");
        Assert.Equal(0xCBF43926u, Crc32.Compute(data));
    }
}
