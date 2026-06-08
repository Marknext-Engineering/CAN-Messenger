namespace CanMessager.Hal;

/// <summary>CAN 인터페이스 하드웨어 종류.</summary>
public enum CanHardwareType
{
    /// <summary>테스트용 가상 루프백 드라이버.</summary>
    Fake,

    /// <summary>Vector VN1630 / VN1640 (XL Driver Library).</summary>
    Vector,

    /// <summary>PEAK-System PCAN-FD.</summary>
    Peak
}

/// <summary>
/// CAN 드라이버를 열 때 사용하는 채널/비트레이트 설정.
/// </summary>
public sealed record CanDriverConfig
{
    /// <summary>하드웨어 종류.</summary>
    public CanHardwareType Hardware { get; init; } = CanHardwareType.Fake;

    /// <summary>채널 인덱스 (0-based). 드라이버별 물리 채널 매핑.</summary>
    public int ChannelIndex { get; init; }

    /// <summary>Arbitration phase 비트레이트 (bps). 일반적으로 500 kbps.</summary>
    public int NominalBitrate { get; init; } = 500_000;

    /// <summary>Data phase 비트레이트 (bps). CAN-FD 고속 구간, 일반적으로 2~5 Mbps.</summary>
    public int DataBitrate { get; init; } = 2_000_000;

    /// <summary>드라이버/애플리케이션 식별 이름 (Vector App 등록명 등).</summary>
    public string AppName { get; init; } = "CanMessager";

    /// <summary>
    /// 드라이버별 네이티브 채널 식별자.
    /// PEAK: PCAN 채널 핸들(0x51 등). Vector: 채널 마스크. (스캔 결과에서 채움)
    /// </summary>
    public ulong NativeChannelId { get; init; }

    /// <summary>
    /// PEAK 전용 FD 비트레이트 문자열 직접 지정 (지정 시 NominalBitrate/DataBitrate 대신 사용).
    /// 예: "f_clock_mhz=80, nom_brp=10, nom_tseg1=12, nom_tseg2=3, nom_sjw=1, data_brp=4, data_tseg1=7, data_tseg2=2, data_sjw=1"
    /// </summary>
    public string? FdBitrateString { get; init; }
}
