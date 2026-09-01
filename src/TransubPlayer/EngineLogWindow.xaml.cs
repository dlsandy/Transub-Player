using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class EngineLogWindow : Window
{
    private readonly DispatcherTimer _autoRefresh;

    public EngineLogWindow()
    {
        InitializeComponent();
        _autoRefresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        _autoRefresh.Tick += (_, _) => RefreshQuiet();
        Loaded += (_, _) =>
        {
            Refresh();
            _autoRefresh.Start();
        };
        Closed += (_, _) => _autoRefresh.Stop();
    }

    public void Refresh() => ApplyText();

    private void RefreshQuiet() => ApplyText();

    private void ApplyText()
    {
        var text = PlayerLog.ReadEngineTail();
        var next = string.IsNullOrWhiteSpace(text) ? Loc.Get("EngineLog.Empty") : text;
        if (!string.Equals(LogBox.Text, next, StringComparison.Ordinal))
            LogBox.Text = next;
        ScrollToLatest();
    }

    private void ScrollToLatest()
    {
        LogBox.CaretIndex = LogBox.Text.Length;
        LogBox.ScrollToEnd();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        PlayerLog.ClearEngine();
        Refresh();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.LogsDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.Format("Errors.OpenFolder", ex.Message), Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
