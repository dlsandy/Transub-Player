using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using TransubPlayer.Services;

namespace TransubPlayer.Desktop;

internal partial class LanShareWindow : Window
{
    private readonly LanShareHost _host;
    private readonly AppSettings _settings;

    public LanShareWindow(LanShareHost host, AppSettings settings)
    {
        _host = host;
        _settings = settings;
        InitializeComponent();
        _host.Changed += Refresh;
        Refresh();
    }

    private async void OnToggle(object? sender, RoutedEventArgs e)
    {
        if (_host.IsRunning)
            await _host.StopAsync();
        else
            _host.Start();
        Refresh();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void Refresh()
    {
        ToggleButton.Content = _host.IsRunning ? "停止" : "启动";
        StatusBlock.Text = _host.SummaryStatus;
        UrlBox.Text = _host.PairUrl ?? "";
        try
        {
            if (!string.IsNullOrWhiteSpace(_host.PairUrl))
            {
                var png = LanQrEncoder.PngBytes(_host.PairUrl, pixelsPerModule: 6);
                using var ms = new MemoryStream(png);
                QrImage.Source = new Bitmap(ms);
            }
            else
            {
                QrImage.Source = null;
            }
        }
        catch
        {
            QrImage.Source = null;
        }
    }
}
