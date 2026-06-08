namespace CanMessager.Core.Transport;

/// <summary>
/// ISO-TP 채널 설정. docs/01_ISO-TP_Layer_Design.md 6장의 타이밍 파라미터에 대응한다.
/// </summary>
public sealed record IsoTpConfig
{
    /// <summary>송신용 CAN ID (이 채널이 프레임을 보낼 때 사용).</summary>
    public required uint TxCanId { get; init; }

    /// <summary>수신용 CAN ID (이 ID의 프레임만 이 채널이 처리).</summary>
    public required uint RxCanId { get; init; }

    /// <summary>29-bit Extended ID 사용 여부. 기본 true.</summary>
    public bool ExtendedId { get; init; } = true;

    /// <summary>Bit Rate Switch 사용 여부. 기본 true.</summary>
    public bool BitRateSwitch { get; init; } = true;

    /// <summary>수신 허용 최대 메시지 길이 (byte). 초과 시 FC(OVFLW) 후 중단. 기본 4 MiB.</summary>
    public int MaxMessageBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>수신측이 FC로 통지할 Block Size (0 = 무제한). 기본 0.</summary>
    public byte BlockSize { get; init; } = 0;

    /// <summary>수신측이 FC로 통지할 STmin (CF 간 최소 간격). 기본 0 = 최대 속도.</summary>
    public byte STmin { get; init; } = 0;

    /// <summary>N_Bs: 송신측 FC 수신 타임아웃 (ms). 기본 1000.</summary>
    public int N_Bs_ms { get; init; } = 1000;

    /// <summary>N_Cr: 수신측 CF 수신 타임아웃 (ms). 기본 1000.</summary>
    public int N_Cr_ms { get; init; } = 1000;

    /// <summary>N_As/N_Ar: CAN 프레임 송신 타임아웃 (ms). 기본 1000.</summary>
    public int N_A_ms { get; init; } = 1000;

    /// <summary>WAIT FC를 연속으로 허용하는 최대 횟수 (초과 시 Abort). 기본 4.</summary>
    public int MaxWaitFrames { get; init; } = 4;

    /// <summary>멀티프레임 마지막 CF의 패딩 바이트. 기본 0xCC.</summary>
    public byte PaddingByte { get; init; } = 0xCC;
}
