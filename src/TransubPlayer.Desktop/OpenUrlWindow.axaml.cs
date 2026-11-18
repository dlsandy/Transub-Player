using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TransubPlayer.Desktop;

internal partial class OpenUrlWindow : Window
{
    public OpenUrlWindow()
    {
        InitializeComponent();
    }

    private void OnOk(object? sender, RoutedEventArgs e)
        => Close(UrlBox.Text?.Trim());

    private void OnCancel(object? sender, RoutedEventArgs e)
        => Close(null);
}
