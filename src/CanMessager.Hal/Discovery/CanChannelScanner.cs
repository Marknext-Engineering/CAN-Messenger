namespace CanMessager.Hal.Discovery;

/// <summary>하드웨어 종류에 맞는 채널 열거자를 선택해 스캔하는 진입점.</summary>
public static class CanChannelScanner
{
    public static ICanChannelEnumerator GetEnumerator(CanHardwareType hardware) => hardware switch
    {
        CanHardwareType.Fake => new FakeChannelEnumerator(),
        CanHardwareType.Peak => new PeakChannelEnumerator(),
        CanHardwareType.Vector => new VectorChannelEnumerator(),
        _ => new FakeChannelEnumerator()
    };

    /// <summary>지정 하드웨어의 채널을 스캔한다. 실패해도 예외 없이 빈 목록 + 진단을 돌려준다.</summary>
    public static IReadOnlyList<CanChannelInfo> Scan(CanHardwareType hardware, out string? diagnostic)
        => GetEnumerator(hardware).Enumerate(out diagnostic);
}
