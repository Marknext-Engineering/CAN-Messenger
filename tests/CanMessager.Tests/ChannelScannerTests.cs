using CanMessager.Hal;
using CanMessager.Hal.Discovery;

namespace CanMessager.Tests;

public class ChannelScannerTests
{
    [Fact]
    public void Fake_Returns_TwoChannels()
    {
        var list = CanChannelScanner.Scan(CanHardwareType.Fake, out var diag);
        Assert.Null(diag);
        Assert.Equal(2, list.Count);
        Assert.All(list, c => Assert.Equal(CanHardwareType.Fake, c.Hardware));
        Assert.Contains(list, c => c.Display.Contains("S/N"));
    }

    [Fact]
    public void Peak_DoesNotThrow_ReturnsListOrDiagnostic()
    {
        // 하드웨어가 없을 수 있으므로 예외 없이 (빈 목록 + 진단) 또는 채널 목록을 반환해야 한다.
        var list = CanChannelScanner.Scan(CanHardwareType.Peak, out var diag);
        Assert.NotNull(list);
        if (list.Count == 0) Assert.False(string.IsNullOrEmpty(diag));
    }

    [Fact]
    public void Vector_DoesNotThrow()
    {
        var ex = Record.Exception(() => CanChannelScanner.Scan(CanHardwareType.Vector, out _));
        Assert.Null(ex);
    }
}
