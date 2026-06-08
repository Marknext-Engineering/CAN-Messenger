using CanMessager.Core.Transport;

namespace CanMessager.Tests;

public class IsoTpPciTests
{
    [Fact]
    public void SingleFrame_Small_RoundTrips()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 }; // 5 bytes → 1-byte PCI
        var raw = IsoTpPci.EncodeSingleFrame(payload);

        Assert.Equal(0x05, raw[0]); // type 0, SF_DL 5
        var f = IsoTpPci.Decode(raw);

        Assert.Equal(IsoTpFrameType.SingleFrame, f.Type);
        Assert.Equal(5, f.Length);
        Assert.Equal(payload, f.Data.ToArray());
    }

    [Fact]
    public void SingleFrame_Escape_RoundTrips()
    {
        var payload = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray(); // > 7 → 2-byte PCI
        var raw = IsoTpPci.EncodeSingleFrame(payload);

        Assert.Equal(0x00, raw[0]);  // escape
        Assert.Equal(40, raw[1]);    // SF_DL
        var f = IsoTpPci.Decode(raw);

        Assert.Equal(IsoTpFrameType.SingleFrame, f.Type);
        Assert.Equal(40, f.Length);
        Assert.Equal(payload, f.Data.ToArray());
    }

    [Fact]
    public void FirstFrame_Small_RoundTrips()
    {
        var payload = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        long total = 1000; // <= 4095 → 2-byte PCI

        var raw = IsoTpPci.EncodeFirstFrame(total, payload, out int consumed);
        Assert.Equal(0x10 | ((total >> 8) & 0x0F), raw[0]);
        Assert.Equal(IsoTpPci.FfMaxDataSmall, consumed);

        var f = IsoTpPci.Decode(raw);
        Assert.Equal(IsoTpFrameType.FirstFrame, f.Type);
        Assert.Equal(total, f.Length);
        Assert.Equal(consumed, f.Data.Length);
    }

    [Fact]
    public void FirstFrame_Escape_RoundTrips()
    {
        var payload = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        long total = 5_000_000; // > 4095 → 6-byte PCI escape

        var raw = IsoTpPci.EncodeFirstFrame(total, payload, out int consumed);
        Assert.Equal(0x10, raw[0]);
        Assert.Equal(0x00, raw[1]);
        Assert.Equal(IsoTpPci.FfMaxData, consumed); // 58

        var f = IsoTpPci.Decode(raw);
        Assert.Equal(IsoTpFrameType.FirstFrame, f.Type);
        Assert.Equal(total, f.Length);
        Assert.Equal(58, f.Data.Length);
    }

    [Fact]
    public void ConsecutiveFrame_RoundTrips()
    {
        var payload = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();
        var raw = IsoTpPci.EncodeConsecutiveFrame(payload, offset: 10, sn: 3, out int consumed,
            paddingByte: 0xCC, padToValidLength: false);

        Assert.Equal(0x23, raw[0]); // type 2, SN 3
        Assert.Equal(IsoTpPci.CfMaxData, consumed); // 63

        var f = IsoTpPci.Decode(raw);
        Assert.Equal(IsoTpFrameType.ConsecutiveFrame, f.Type);
        Assert.Equal(3, f.SequenceNumber);
        Assert.Equal(payload.Skip(10).Take(63).ToArray(), f.Data.ToArray());
    }

    [Fact]
    public void FlowControl_RoundTrips()
    {
        var raw = IsoTpPci.EncodeFlowControl(FlowStatus.ContinueToSend, blockSize: 8, stMin: 0x14);
        Assert.Equal(0x30, raw[0]);

        var f = IsoTpPci.Decode(raw);
        Assert.Equal(IsoTpFrameType.FlowControl, f.Type);
        Assert.Equal(FlowStatus.ContinueToSend, f.FlowStatus);
        Assert.Equal(8, f.BlockSize);
        Assert.Equal(0x14, f.STmin);
    }

    [Theory]
    [InlineData(0x00, 0)]
    [InlineData(0x7F, 127)]
    public void DecodeSTmin_Milliseconds(byte stMin, int expectedMs)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), IsoTpPci.DecodeSTmin(stMin));
    }

    [Theory]
    [InlineData(0xF1, 100)]
    [InlineData(0xF9, 900)]
    public void DecodeSTmin_Microseconds(byte stMin, int expectedUs)
    {
        Assert.Equal(TimeSpan.FromMicroseconds(expectedUs), IsoTpPci.DecodeSTmin(stMin));
    }
}
