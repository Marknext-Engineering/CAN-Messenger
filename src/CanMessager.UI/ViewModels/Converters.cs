using System.Globalization;
using System.Windows.Data;
using CanMessager.Hal;
using CanMessager.UI.Services;

namespace CanMessager.UI.ViewModels;

/// <summary>bool 값을 반전하는 컨버터 (연결 중이면 설정 입력 비활성화 등).</summary>
public sealed class BoolNot : IValueConverter
{
    public static readonly BoolNot Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}

/// <summary>역할/하드웨어 enum을 한글 라벨로 표시.</summary>
public sealed class EnumKoreanConverter : IValueConverter
{
    public static readonly EnumKoreanConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        TransferRole.Sender => "송신처 (보내기)",
        TransferRole.Receiver => "수신처 (받기)",
        TransferRole.LoopbackDemo => "루프백 데모",
        CanHardwareType.Fake => "Fake (가상)",
        CanHardwareType.Vector => "Vector VN16xx",
        CanHardwareType.Peak => "PEAK PCAN",
        _ => value?.ToString() ?? ""
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
