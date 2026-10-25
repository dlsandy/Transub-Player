using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class LanShareWindow : Window
{
    private readonly AppSettings _settings;
    private readonly LanShareHost _host;
    private readonly Action _openSettings;
    private bool _syncing;

    public LanShareWindow(AppSettings settings, LanShareHost host, Action openSettings)
    {
        _settings = settings;
        _host = host;
        _openSettings = openSettings;
        InitializeComponent();
        _host.Changed += OnHostChanged;
        Closed += (_, _) => _host.Changed -= OnHostChanged;
        Loaded += (_, _) => EnsureServerMatchesCheckbox();
        _syncing = true;
        EnabledBox.IsChecked = _settings.LanShareEnabled;
        PasswordBox.Password = _settings.LanSharePassword ?? "";
        _syncing = false;
        RefreshRoots();
        RefreshUi();
    }

    private void OnHostChanged()
        => Dispatcher.BeginInvoke(RefreshUi);

    /// <summary>
    /// Settings checkbox can say "on" while the HTTP listener failed earlier —
    /// reopen should actually start (or surface the bind error).
    /// </summary>
    private void EnsureServerMatchesCheckbox()
    {
        if (_settings.LanShareEnabled && !_host.IsRunning)
        {
            try
            {
                LanShareNet.EnsureToken(_settings);
                _host.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    ex.Message,
                    Loc.Get("Lan.Window.Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                _syncing = true;
                EnabledBox.IsChecked = false;
                _settings.LanShareEnabled = false;
                _settings.Save();
                _syncing = false;
            }
        }

        _syncing = true;
        EnabledBox.IsChecked = _host.IsRunning;
        _syncing = false;
        RefreshUi();
    }

    private void RefreshRoots()
    {
        RootsList.Items.Clear();
        foreach (var root in LanPathGuard.NormalizedRoots(_settings.LanShareRoots))
            RootsList.Items.Add(root);
        if (RootsList.Items.Count == 0)
            RootsList.Items.Add(Loc.Get("Lan.Window.NoRoots"));
    }

    private void RefreshUi()
    {
        var hasPassword = !string.IsNullOrWhiteSpace(_settings.LanSharePassword);
        PasswordStateText.Text = hasPassword
            ? Loc.Get("Lan.Window.PasswordOn")
            : Loc.Get("Lan.Window.PasswordOff");

        SessionSummaryText.Text = _host.SummaryStatus;
        if (!string.IsNullOrWhiteSpace(_host.LastError) && !_host.IsRunning)
        {
            SessionDetailText.Text = _host.LastError;
        }
        else if (_host.IsRunning && !_host.PhoneReachable)
        {
            SessionDetailText.Text = Loc.Get("Lan.Window.LoopbackOnlyHint");
        }
        else if (_host.HasActiveSession)
        {
            var detail = _host.SessionStatus ?? "";
            if (_host.ViewerCount > 0)
                detail = string.IsNullOrWhiteSpace(detail)
                    ? Loc.Format("Lan.Window.Viewers", _host.ViewerCount)
                    : detail + " · " + Loc.Format("Lan.Window.Viewers", _host.ViewerCount);
            SessionDetailText.Text = detail;
        }
        else if (_host.IsRunning && _host.ViewerCount > 0)
        {
            SessionDetailText.Text = Loc.Format("Lan.Window.ViewersBrowsing", _host.ViewerCount);
        }
        else
        {
            SessionDetailText.Text = _host.IsRunning
                ? Loc.Get("Lan.Window.SessionIdle")
                : Loc.Get("Lan.Window.SessionOff");
        }

        if (_host.IsRunning)
        {
            StateText.Text = _host.PhoneReachable
                ? Loc.Format("Lan.Status.Listening", _host.LanIp ?? "?", _host.Port)
                : Loc.Format("Lan.Status.LoopbackOnly", _host.Port);
            UrlText.Text = _host.PairUrl ?? "";
            var png = _host.QrPng();
            if (png is { Length: > 0 })
            {
                var bmp = new BitmapImage();
                using var ms = new MemoryStream(png);
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                QrImage.Source = bmp;
            }
            else
            {
                QrImage.Source = null;
            }
        }
        else
        {
            StateText.Text = string.IsNullOrWhiteSpace(_host.LastError)
                ? Loc.Get("Lan.Status.Stopped")
                : Loc.Format("Lan.Status.StartFailed", _host.LastError);
            UrlText.Text = Loc.Get("Lan.Window.UrlWhenOff");
            QrImage.Source = null;
        }
    }

    private void SavePassword_Click(object sender, RoutedEventArgs e)
    {
        _settings.LanSharePassword = PasswordBox.Password ?? "";
        _settings.Save();
        if (_host.IsRunning)
            _host.RefreshAuthFromSettings();
        RefreshUi();
        MessageBox.Show(
            this,
            Loc.Get("Lan.Window.PasswordSaved"),
            Loc.Get("Lan.Window.Title"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void Enabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var on = EnabledBox.IsChecked == true;

        // Password is optional: empty = QR invite token only; typed value is kept as-is.
        _settings.LanSharePassword = PasswordBox.Password ?? "";
        _settings.LanShareEnabled = on;
        if (on)
            LanShareNet.EnsureToken(_settings);
        _settings.Save();

        try
        {
            if (on)
            {
                if (LanPathGuard.NormalizedRoots(_settings.LanShareRoots).Count == 0)
                {
                    MessageBox.Show(
                        this,
                        Loc.Get("Lan.Window.NeedRoots"),
                        Loc.Get("Lan.Window.Title"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                _host.Start();
            }
            else
            {
                MessageBox.Show(
                    this,
                    Loc.Get("Lan.Window.ClosePhoneHint"),
                    Loc.Get("Lan.Window.Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                await _host.StopAsync(announcePhoneDisconnect: true).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                Loc.Get("Lan.Window.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            _syncing = true;
            EnabledBox.IsChecked = _host.IsRunning;
            _settings.LanShareEnabled = _host.IsRunning;
            _settings.Save();
            _syncing = false;
        }

        RefreshUi();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        _openSettings();
        PasswordBox.Password = _settings.LanSharePassword ?? "";
        RefreshRoots();
        EnsureServerMatchesCheckbox();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
