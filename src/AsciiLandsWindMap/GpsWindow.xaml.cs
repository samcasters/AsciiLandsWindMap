using System.Windows;

namespace AsciiLandsWindMap;

public enum GpsChoice
{
    Cancel,
    Boat,
    Walk,
    Clear,
}

public partial class GpsWindow : Window
{
    public GpsChoice Choice { get; private set; } = GpsChoice.Cancel;

    public GpsWindow(bool canClear)
    {
        InitializeComponent();
        if (!canClear)
            ClearButton.Visibility = Visibility.Collapsed;
    }

    private void BoatClick(object sender, RoutedEventArgs e)
    {
        Choice = GpsChoice.Boat;
        DialogResult = true;
    }

    private void WalkClick(object sender, RoutedEventArgs e)
    {
        Choice = GpsChoice.Walk;
        DialogResult = true;
    }

    private void ClearClick(object sender, RoutedEventArgs e)
    {
        Choice = GpsChoice.Clear;
        DialogResult = true;
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        Choice = GpsChoice.Cancel;
        DialogResult = false;
    }
}
