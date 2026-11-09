using System.Windows;
using System.Windows.Media;

namespace AsciiLandsWindMap;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplyTheme(false);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    public static void ApplyTheme(bool dark)
    {
        Set("BgBrush", dark ? Rgb(18, 22, 28) : Rgb(243, 240, 232));
        Set("PanelBrush", dark ? Rgb(28, 35, 45) : Rgb(255, 252, 247));
        Set("TextBrush", dark ? Rgb(232, 226, 214) : Rgb(28, 25, 21));
        Set("MutedBrush", dark ? Rgb(163, 155, 140) : Rgb(94, 88, 78));
        Set("AccentBrush", dark ? Rgb(47, 143, 114) : Rgb(31, 111, 91));
        Set("InputBrush", dark ? Rgb(18, 22, 28) : Rgb(255, 255, 255));
        Set("LineBrush", dark ? Rgb(52, 60, 72) : Rgb(217, 210, 197));
        Set("DangerBrush", dark ? Rgb(224, 122, 122) : Rgb(156, 59, 59));
    }

    private static void Set(string key, Color color)
    {
        if (Current.Resources[key] is SolidColorBrush brush && !brush.IsFrozen)
            brush.Color = color;
        else
            Current.Resources[key] = new SolidColorBrush(color);
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}
