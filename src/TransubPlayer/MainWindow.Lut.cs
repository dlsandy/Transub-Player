using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class MainWindow
{
    private ColorGradeWindow? _colorGradeWindow;

    private void RefreshLutMenu()
    {
        if (ColorGradeMenu is null) return;

        ColorGradeMenu.Items.Clear();
        LutPresets.EnsureInstalled();

        var active = _preview?.ActiveLutPath;
        var fromSidecar = _preview?.LutFromSidecar == true;
        var grade = _settings.GetColorGrade();

        var adjust = new MenuItem { Header = Loc.Get("Main.Menu.ColorGradeAdjust") };
        adjust.Click += (_, _) => OpenColorGradeWindow();
        ColorGradeMenu.Items.Add(adjust);

        var resetGrade = new MenuItem
        {
            Header = Loc.Get("Main.Menu.ColorGradeReset"),
            IsEnabled = !grade.IsDefault,
        };
        resetGrade.Click += (_, _) =>
        {
            _preview?.ResetColorGrade(announce: true);
            _colorGradeWindow?.ReloadFromSettings();
            RefreshLutMenu();
        };
        ColorGradeMenu.Items.Add(resetGrade);

        var saved = ColorGradeStore.List();
        if (saved.Count > 0)
        {
            ColorGradeMenu.Items.Add(new Separator());
            foreach (var (name, path, parameters) in saved)
            {
                var item = new MenuItem
                {
                    Header = name,
                    IsCheckable = true,
                    IsChecked = grade.EqualsValues(parameters),
                    Tag = path,
                };
                var capturedParams = parameters;
                var capturedPath = path;
                var capturedName = name;
                item.Click += (_, _) => ApplySavedColorGrade(capturedName, capturedParams);
                var del = new MenuItem { Header = Loc.Get("Main.Menu.ColorGradeDeletePreset") };
                del.Click += (_, _) =>
                {
                    ColorGradeStore.Delete(capturedPath);
                    RefreshLutMenu();
                };
                item.Items.Add(del);
                ColorGradeMenu.Items.Add(item);
            }
        }

        ColorGradeMenu.Items.Add(new Separator());

        var off = new MenuItem
        {
            Header = Loc.Get("Main.Menu.LutOff"),
            IsCheckable = true,
            IsChecked = string.IsNullOrWhiteSpace(active),
        };
        off.Click += (_, _) => ApplyLutChoice(null);
        ColorGradeMenu.Items.Add(off);

        if (fromSidecar && !string.IsNullOrWhiteSpace(active))
        {
            ColorGradeMenu.Items.Add(new MenuItem
            {
                Header = Loc.Format("Main.Menu.LutSidecarCurrent", Path.GetFileName(active)),
                IsCheckable = true,
                IsChecked = true,
                IsEnabled = false,
            });
        }

        foreach (var (preset, path) in LutPresets.ListInstalled())
        {
            var item = new MenuItem
            {
                Header = Loc.Get(preset.LabelKey),
                IsCheckable = true,
                IsChecked = !fromSidecar
                            && PathsEqual(active, path),
                Tag = path,
            };
            var captured = path;
            item.Click += (_, _) => ApplyLutChoice(captured);
            ColorGradeMenu.Items.Add(item);
        }

        // Custom saved path that is not one of the shipped presets.
        if (!string.IsNullOrWhiteSpace(_settings.LutPath)
            && File.Exists(_settings.LutPath)
            && LutPresets.IdForPath(_settings.LutPath) is null
            && !(fromSidecar && PathsEqual(active, _settings.LutPath)))
        {
            var custom = new MenuItem
            {
                Header = Path.GetFileName(_settings.LutPath),
                IsCheckable = true,
                IsChecked = !fromSidecar && PathsEqual(active, _settings.LutPath),
                Tag = _settings.LutPath,
            };
            var captured = _settings.LutPath;
            custom.Click += (_, _) => ApplyLutChoice(captured);
            ColorGradeMenu.Items.Add(custom);
        }

        ColorGradeMenu.Items.Add(new Separator());

        var browse = new MenuItem { Header = Loc.Get("Main.Menu.LutBrowse") };
        browse.Click += LutBrowse_Click;
        ColorGradeMenu.Items.Add(browse);

        var openFolder = new MenuItem { Header = Loc.Get("Main.Menu.LutOpenFolder") };
        openFolder.Click += LutOpenFolder_Click;
        ColorGradeMenu.Items.Add(openFolder);

        var auto = new MenuItem
        {
            Header = Loc.Get("Main.Menu.LutAutoSidecar"),
            IsCheckable = true,
            IsChecked = _settings.LutAutoSidecar,
            ToolTip = Loc.Get("Main.Menu.LutAutoSidecar.Tip"),
        };
        auto.Click += LutAutoSidecar_Click;
        ColorGradeMenu.Items.Add(auto);

        ColorGradeMenu.Header = BuildColorGradeHeader(active, fromSidecar, grade);
    }

    private static string BuildColorGradeHeader(string? activeLut, bool fromSidecar, ColorGradeParams grade)
    {
        string? lutLabel = null;
        if (!string.IsNullOrWhiteSpace(activeLut))
        {
            lutLabel = fromSidecar
                ? Loc.Get("Main.Menu.LutSidecarShort")
                : LutPresets.IdForPath(activeLut) is { } id
                    ? Loc.Get(LutPresets.Catalog.First(p => p.Id == id).LabelKey)
                    : Path.GetFileName(activeLut);
        }

        if (lutLabel is not null && !grade.IsDefault)
            return Loc.Format(
                "Main.Menu.ColorGradeCurrent",
                Loc.Format("Main.Menu.ColorGradeLutAndManual", lutLabel));
        if (lutLabel is not null)
            return Loc.Format("Main.Menu.ColorGradeCurrent", lutLabel);
        if (!grade.IsDefault)
            return Loc.Format("Main.Menu.ColorGradeCurrent", Loc.Get("Main.Menu.ColorGradeManualShort"));
        return Loc.Get("Main.Menu.ColorGrade");
    }

    private void OpenColorGradeWindow()
    {
        if (_preview is null) return;
        if (_colorGradeWindow is not null)
        {
            _colorGradeWindow.ReloadFromSettings();
            _colorGradeWindow.Activate();
            return;
        }

        var preview = _preview;
        var win = new ColorGradeWindow(
            _settings,
            apply: p => preview.SetColorGrade(p, persist: true, announce: false),
            reset: () => preview.ResetColorGrade(announce: true))
        {
            Owner = this,
        };
        win.Saved += () => Dispatcher.Invoke(RefreshLutMenu);
        win.Closed += (_, _) =>
        {
            _colorGradeWindow = null;
            RefreshLutMenu();
        };
        _colorGradeWindow = win;
        win.Show();
    }

    private void ApplySavedColorGrade(string name, ColorGradeParams parameters)
    {
        if (_preview is null) return;
        _preview.SetColorGrade(parameters, persist: true, announce: false);
        _colorGradeWindow?.ReloadFromSettings();
        _preview.ShowOsd(Loc.Format("Main.Osd.ColorGradePreset", name), 1800);
        RefreshLutMenu();
    }

    private void ApplyLutChoice(string? path)
    {
        if (_preview is null) return;
        try
        {
            _preview.SetLutPath(path, persist: true);
            RefreshLutMenu();
        }
        catch (Exception ex)
        {
            SetStatus(UserFacingErrors.Message(ex));
        }
    }

    private void LutBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.Get("Main.Menu.LutBrowse"),
            Filter = Loc.Get("Main.Menu.LutFilter"),
            CheckFileExists = true,
            Multiselect = false,
        };
        try
        {
            var initial = AppPaths.LutsDir;
            if (Directory.Exists(initial))
                dlg.InitialDirectory = initial;
        }
        catch
        {
            // ignore
        }

        if (dlg.ShowDialog(this) != true) return;
        ApplyLutChoice(dlg.FileName);
    }

    private void LutOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            LutPresets.EnsureInstalled();
            var dir = AppPaths.LutsDir;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                Loc.Format("Errors.OpenFolder", ex.Message),
                "Transub Player",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void LutAutoSidecar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        _preview?.SetLutAutoSidecar(item.IsChecked);
        RefreshLutMenu();
    }

    private static bool PathsEqual(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(a),
                Path.GetFullPath(b),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
