namespace CanMessager.Hal;

/// <summary>
/// CAN-FD DLC(Data Length Code) ↔ 실제 바이트 길이 변환 유틸리티.
/// CAN-FD는 9~15 DLC에서 12/16/20/24/32/48/64 byte 의 비연속 길이를 가진다.
/// </summary>
public static class CanFdLength
{
    /// <summary>CAN-FD에서 사용 가능한 페이로드 길이 목록 (오름차순).</summary>
    public static readonly int[] ValidLengths =
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 20, 24, 32, 48, 64 };

    /// <summary>CAN-FD 최대 페이로드 길이 (byte).</summary>
    public const int MaxPayload = 64;

    /// <summary>
    /// 주어진 데이터 길이를 담을 수 있는 가장 작은 유효 CAN-FD 길이를 반환한다.
    /// (전송 시 패딩 대상 길이 결정에 사용)
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">length가 0 미만이거나 64 초과인 경우.</exception>
    public static int Ceil(int length)
    {
        if (length < 0 || length > MaxPayload)
            throw new ArgumentOutOfRangeException(nameof(length), length,
                $"CAN-FD payload length must be between 0 and {MaxPayload}.");

        foreach (var valid in ValidLengths)
        {
            if (valid >= length)
                return valid;
        }

        return MaxPayload; // 도달 불가 (위 범위 검사로 보장)
    }

    /// <summary>주어진 길이가 CAN-FD에서 직접 표현 가능한 유효 길이인지 여부.</summary>
    public static bool IsValid(int length) => Array.IndexOf(ValidLengths, length) >= 0;

    /// <summary>바이트 길이 → CAN-FD DLC 코드(0..15). 길이는 유효한 CAN-FD 길이여야 한다.</summary>
    public static byte LengthToDlc(int length) => length switch
    {
        <= 8 => (byte)length,
        12 => 9,
        16 => 10,
        20 => 11,
        24 => 12,
        32 => 13,
        48 => 14,
        64 => 15,
        _ => (byte)Ceil(length) // 비표준 길이는 올림 후 변환
            switch
        {
            12 => 9, 16 => 10, 20 => 11, 24 => 12, 32 => 13, 48 => 14, 64 => 15,
            var n => (byte)n
        }
    };

    /// <summary>CAN-FD DLC 코드(0..15) → 바이트 길이.</summary>
    public static int DlcToLength(byte dlc) => dlc switch
    {
        <= 8 => dlc,
        9 => 12,
        10 => 16,
        11 => 20,
        12 => 24,
        13 => 32,
        14 => 48,
        15 => 64,
        _ => 0
    };
}
