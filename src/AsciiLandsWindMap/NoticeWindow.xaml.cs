using System.Windows;

namespace AsciiLandsWindMap;

public partial class NoticeWindow : Window
{
    public NoticeWindow(bool viewOnly = false)
    {
        InitializeComponent();
        Heading.Text = NoticeText.Title;
        BodyBox.Text = NoticeText.Body;
        if (!viewOnly)
            return;
        ChoiceBar.Visibility = Visibility.Collapsed;
        CloseBar.Visibility = Visibility.Visible;
        DeclineButton.IsCancel = false;
        Title = "Notice";
    }

    private void AcceptClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void DeclineClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void CloseClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
