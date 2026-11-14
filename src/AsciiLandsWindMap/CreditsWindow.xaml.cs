using System.Windows;

namespace AsciiLandsWindMap;

public partial class CreditsWindow : Window
{
    public CreditsWindow()
    {
        InitializeComponent();
    }

    private void CloseClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
