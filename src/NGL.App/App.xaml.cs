using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace NGL.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 모든 창의 기본 글꼴과 제목 표시줄을 한곳에서 맞춘다. 코드로 만든 창에도 적용된다.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
        base.OnStartup(e);
    }

    /// <summary>제목 표시줄을 어둡게 한다. 지원하지 않는 윈도우에서는 아무 일도 일어나지 않는다.</summary>
    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window) return;
        window.FontFamily = (FontFamily)Current.FindResource("Ui");
        var dark = 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, UseImmersiveDarkMode, ref dark, sizeof(int));
    }

    private const int UseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
