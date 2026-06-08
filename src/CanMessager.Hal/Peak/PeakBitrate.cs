namespace CanMessager.Hal.Peak;

/// <summary>
/// PCAN-FD 비트레이트 문자열 생성. PCAN-USB (Pro) FD는 80 MHz 클럭 기준.
/// (nominal, data) 조합에 대한 사전 정의 타이밍을 제공하고, 없으면 500k/2M로 폴백한다.
/// </summary>
public static class PeakBitrate
{
    // nominal 공통: 80MHz, brp/tseg는 비트레이트별.
    private static readonly Dictionary<(int nom, int data), string> Presets = new()
    {
        [(500_000, 2_000_000)] = "f_clock_mhz=80, nom_brp=10, nom_tseg1=12, nom_tseg2=3, nom_sjw=1, data_brp=4, data_tseg1=7, data_tseg2=2, data_sjw=1",
        [(500_000, 4_000_000)] = "f_clock_mhz=80, nom_brp=10, nom_tseg1=12, nom_tseg2=3, nom_sjw=1, data_brp=2, data_tseg1=7, data_tseg2=2, data_sjw=1",
        [(500_000, 5_000_000)] = "f_clock_mhz=80, nom_brp=10, nom_tseg1=12, nom_tseg2=3, nom_sjw=1, data_brp=2, data_tseg1=5, data_tseg2=2, data_sjw=2",
        [(500_000, 8_000_000)] = "f_clock_mhz=80, nom_brp=10, nom_tseg1=12, nom_tseg2=3, nom_sjw=1, data_brp=1, data_tseg1=7, data_tseg2=2, data_sjw=1",
        [(1_000_000, 2_000_000)] = "f_clock_mhz=80, nom_brp=5, nom_tseg1=12, nom_tseg2=3, nom_sjw=1, data_brp=4, data_tseg1=7, data_tseg2=2, data_sjw=1",
        [(1_000_000, 4_000_000)] = "f_clock_mhz=80, nom_brp=5, nom_tseg1=12, nom_tseg2=3, nom_sjw=1, data_brp=2, data_tseg1=7, data_tseg2=2, data_sjw=1",
        [(1_000_000, 5_000_000)] = "f_clock_mhz=80, nom_brp=5, nom_tseg1=12, nom_tseg2=3, nom_sjw=1, data_brp=2, data_tseg1=5, data_tseg2=2, data_sjw=2",
        [(1_000_000, 8_000_000)] = "f_clock_mhz=80, nom_brp=5, nom_tseg1=12, nom_tseg2=3, nom_sjw=1, data_brp=1, data_tseg1=7, data_tseg2=2, data_sjw=1",
        [(250_000, 2_000_000)] = "f_clock_mhz=80, nom_brp=20, nom_tseg1=12, nom_tseg2=3, nom_sjw=1, data_brp=4, data_tseg1=7, data_tseg2=2, data_sjw=1",
    };

    /// <summary>(nominal, data) 조합의 비트레이트 문자열. 없으면 500k/2M로 폴백하고 fallback=true.</summary>
    public static string Get(int nominal, int data, out bool fallback)
    {
        if (Presets.TryGetValue((nominal, data), out var s))
        {
            fallback = false;
            return s;
        }
        fallback = true;
        return Presets[(500_000, 2_000_000)];
    }
}
