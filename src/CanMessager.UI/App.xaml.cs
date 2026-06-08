using System.Windows;
using System.Windows.Threading;

namespace CanMessager.UI;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // UI 스레드 미처리 예외: 메시지로 표시하고 앱은 유지 (조용한 종료 방지).
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 백그라운드 스레드 미처리 예외.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                ShowError("백그라운드 오류", ex);
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowError("UI 오류", e.Exception);
        e.Handled = true;
    }

    private static void ShowError(string title, Exception ex)
    {
        MessageBox.Show($"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
            $"CANoe HEX ReFlash Manager — {title}", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
