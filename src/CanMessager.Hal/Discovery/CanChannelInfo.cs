namespace CanMessager.Hal.Discovery;

/// <summary>
/// 드라이버가 보고한 물리 CAN 채널 1개의 정보. UI에서 선택 가능한 단위이며,
/// 실제 채널을 열 때 필요한 식별자(채널 인덱스 / Vector 채널 마스크 / PEAK 핸들)를 담는다.
/// </summary>
public sealed record CanChannelInfo
{
    public required CanHardwareType Hardware { get; init; }

    /// <summary>드라이버 채널 인덱스(0-based). 채널을 열 때 사용.</summary>
    public required int ChannelIndex { get; init; }

    /// <summary>장치 모델/채널 표시명 (예: "VN1640A", "PCAN-USB FD").</summary>
    public required string DeviceName { get; init; }

    /// <summary>하드웨어 시리얼 번호 (없으면 "N/A").</summary>
    public string SerialNumber { get; init; } = "N/A";

    /// <summary>장치 내 물리 채널 번호 (1-based 표시용).</summary>
    public int PhysicalChannel { get; init; }

    /// <summary>Vector 전용: xlOpenPort에 사용하는 채널 마스크.</summary>
    public ulong VectorChannelMask { get; init; }

    /// <summary>PEAK 전용: PCAN 채널 핸들.</summary>
    public ushort PeakHandle { get; init; }

    /// <summary>현재 버스에 연결/사용 가능 상태인지.</summary>
    public bool IsAvailable { get; init; } = true;

    /// <summary>콤보박스 등에 표시할 한 줄 설명.</summary>
    public string Display =>
        $"{DeviceName} · Ch{PhysicalChannel} · S/N {SerialNumber}" + (IsAvailable ? "" : " (사용불가)");
}
