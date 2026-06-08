namespace CanMessager.Hal.Discovery;

/// <summary>특정 하드웨어 종류의 사용 가능한 CAN 채널을 열거한다.</summary>
public interface ICanChannelEnumerator
{
    CanHardwareType Hardware { get; }

    /// <summary>
    /// 채널 목록을 스캔한다. 드라이버/장치가 없으면 빈 목록을 반환하며,
    /// 진단 메시지가 있으면 <paramref name="diagnostic"/>에 채운다 (예외를 던지지 않음).
    /// </summary>
    IReadOnlyList<CanChannelInfo> Enumerate(out string? diagnostic);
}
