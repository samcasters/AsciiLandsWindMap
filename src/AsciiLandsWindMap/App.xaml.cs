using System.IO;
using System.Windows;
using System.Windows.Media;

namespace AsciiLandsWindMap;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        ApplyTheme(false);
        if (!AcceptedNotice())
        {
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        ShutdownMode = ShutdownMode.OnMainWindowClose;
    }

    private static bool AcceptedNotice()
    {
        var folder = WindDatabase.AppFolder();
        if (NoticeStore.IsAccepted(folder))
            return true;

        var notice = new NoticeWindow();
        if (notice.ShowDialog() != true)
            return false;

        try
        {
            NoticeStore.Accept(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                "Could not save your acceptance on this PC, so the app will close. Nothing was read from the website.",
                "Before you use AsciiLands Wind Map",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    /// <summary>
    /// The play page uses one dark HUD for day and night, so both values paint the same colors.
    /// Hex biomes still darken at night on their own.
    /// </summary>
    public static void ApplyTheme(bool dark)
    {
        _ = dark;
        Set("BgBrush", Rgb(14, 18, 10));
        Set("PanelBrush", Rgb(22, 28, 15));
        Set("TextBrush", Rgb(238, 245, 216));
        Set("MutedBrush", Rgb(185, 201, 160));
        Set("AccentBrush", Rgb(157, 228, 108));
        Set("WarmBrush", Rgb(255, 209, 121));
        Set("InputBrush", Rgb(12, 18, 10));
        Set("LineBrush", Argb(128, 148, 198, 88));
        Set("DangerBrush", Rgb(224, 122, 122));
        Set("InkBrush", Rgb(14, 18, 10));
    }

    private static void Set(string key, Color color)
    {
        if (Current.Resources[key] is SolidColorBrush brush && !brush.IsFrozen)
            brush.Color = color;
        else
            Current.Resources[key] = new SolidColorBrush(color);
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static Color Argb(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);
}
