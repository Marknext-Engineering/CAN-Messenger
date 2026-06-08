using CanMessager.Hal;

namespace CanMessager.Tests;

public class CanFdLengthTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(8, 8)]
    [InlineData(12, 9)]
    [InlineData(16, 10)]
    [InlineData(20, 11)]
    [InlineData(24, 12)]
    [InlineData(32, 13)]
    [InlineData(48, 14)]
    [InlineData(64, 15)]
    public void LengthToDlc_And_Back(int length, byte dlc)
    {
        Assert.Equal(dlc, CanFdLength.LengthToDlc(length));
        Assert.Equal(length, CanFdLength.DlcToLength(dlc));
    }

    [Theory]
    [InlineData(9, 9)]    // 9바이트 → 올림(12) → DLC 9
    [InlineData(33, 14)]  // → 48 → DLC 14
    [InlineData(63, 15)]  // → 64 → DLC 15
    public void LengthToDlc_RoundsUp_NonStandard(int length, byte expectedDlc)
    {
        Assert.Equal(expectedDlc, CanFdLength.LengthToDlc(length));
    }
}
