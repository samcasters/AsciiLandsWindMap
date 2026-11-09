using System.Windows;

namespace AsciiLandsWindMap;

public partial class ClockWindow : Window
{
    public int Minute { get; private set; }
    public double? DayLengthMinutes { get; private set; }

    public ClockWindow(int? currentMinute, double? dayLength)
    {
        InitializeComponent();
        TimeBox.Text = GameClock.Format(currentMinute ?? WindRules.DayViewMinute);
        if (dayLength is double length)
            LengthBox.Text = length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        TimeBox.SelectAll();
        Loaded += (_, _) => TimeBox.Focus();
    }

    private void OkClick(object sender, RoutedEventArgs e)
    {
        if (!GameClock.TryParseTime(TimeBox.Text, out var minute))
        {
            ErrorText.Text = "Enter the time as HH:MM, for example 14:05.";
            return;
        }
        if (!GameClock.TryParseDayLength(LengthBox.Text, out var length))
        {
            ErrorText.Text = "Day length should be a number of real minutes, or blank.";
            return;
        }
        Minute = minute;
        DayLengthMinutes = length;
        DialogResult = true;
    }

    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
