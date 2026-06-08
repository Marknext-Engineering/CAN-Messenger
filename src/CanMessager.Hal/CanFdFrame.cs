namespace CanMessager.Hal;

/// <summary>
/// 단일 CAN-FD 프레임을 표현하는 불변 값 타입.
/// HAL 계층의 송수신 기본 단위이며, 상위 ISO-TP 레이어가 이 프레임을 생성/해석한다.
/// </summary>
public readonly record struct CanFdFrame
{
    /// <summary>CAN Arbitration ID (11-bit 또는 29-bit).</summary>
    public uint CanId { get; init; }

    /// <summary>29-bit Extended ID 여부 (false면 11-bit Standard).</summary>
    public bool ExtendedId { get; init; }

    /// <summary>CAN-FD 프레임 여부. 본 프로젝트에서는 항상 true.</summary>
    public bool Fd { get; init; }

    /// <summary>Bit Rate Switch(BRS) — Data phase 고속 전환 여부. CAN-FD에서 권장 true.</summary>
    public bool BitRateSwitch { get; init; }

    /// <summary>프레임 페이로드 (최대 64 byte).</summary>
    public ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>수신 시 드라이버가 채우는 타임스탬프 (송신 시에는 무시).</summary>
    public DateTimeOffset Timestamp { get; init; }

    public CanFdFrame(uint canId, ReadOnlyMemory<byte> data,
        bool extendedId = true, bool fd = true, bool bitRateSwitch = true)
    {
        if (data.Length > CanFdLength.MaxPayload)
            throw new ArgumentOutOfRangeException(nameof(data),
                $"CAN-FD payload cannot exceed {CanFdLength.MaxPayload} bytes (got {data.Length}).");

        CanId = canId;
        Data = data;
        ExtendedId = extendedId;
        Fd = fd;
        BitRateSwitch = bitRateSwitch;
        Timestamp = default;
    }
}
