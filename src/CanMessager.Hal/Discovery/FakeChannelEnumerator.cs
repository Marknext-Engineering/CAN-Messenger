namespace CanMessager.Hal.Discovery;

/// <summary>테스트/데모용 가상 채널 열거자 — 항상 두 개의 가상 채널을 보고한다.</summary>
public sealed class FakeChannelEnumerator : ICanChannelEnumerator
{
    public CanHardwareType Hardware => CanHardwareType.Fake;

    public IReadOnlyList<CanChannelInfo> Enumerate(out string? diagnostic)
    {
        diagnostic = null;
        return new[]
        {
            new CanChannelInfo
            {
                Hardware = CanHardwareType.Fake,
                ChannelIndex = 0,
                DeviceName = "가상 CAN-FD 장치",
                SerialNumber = "FAKE-0001",
                PhysicalChannel = 1
            },
            new CanChannelInfo
            {
                Hardware = CanHardwareType.Fake,
                ChannelIndex = 1,
                DeviceName = "가상 CAN-FD 장치",
                SerialNumber = "FAKE-0001",
                PhysicalChannel = 2
            }
        };
    }
}
