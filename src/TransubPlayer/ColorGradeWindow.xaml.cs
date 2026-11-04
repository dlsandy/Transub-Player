using System.Windows;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class ColorGradeWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action<ColorGradeParams> _apply;
    private readonly Action _reset;
    private bool _suppress;

    public ColorGradeWindow(AppSettings settings, Action<ColorGradeParams> apply, Action reset)
    {
        _settings = settings;
        _apply = apply;
        _reset = reset;
        InitializeComponent();
        LoadFromSettings();
    }

    /// <summary>Fired after a named preset is written so the owner can refresh the 调色 menu.</summary>
    public event Action? Saved;

    public void ReloadFromSettings() => LoadFromSettings();

    private void LoadFromSettings()
    {
        _suppress = true;
        try
        {
            var p = _settings.GetColorGrade();
            BrightnessSlider.Value = p.Brightness;
            ContrastSlider.Value = p.Contrast;
            SaturationSlider.Value = p.Saturation;
            GammaSlider.Value = p.Gamma;
            HueSlider.Value = p.Hue;
            ShadowsSlider.Value = p.Shadows;
            SharpnessSlider.Value = p.Sharpness;
            RefreshValueLabels();
        }
        finally
        {
            _suppress = false;
        }
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress || !IsLoaded) return;
        RefreshValueLabels();
        ApplyLive();
    }

    private void RefreshValueLabels()
    {
        BrightnessValue.Text = Format(BrightnessSlider.Value);
        ContrastValue.Text = Format(ContrastSlider.Value);
        SaturationValue.Text = Format(SaturationSlider.Value);
        GammaValue.Text = Format(GammaSlider.Value);
        HueValue.Text = Format(HueSlider.Value);
        ShadowsValue.Text = Format(ShadowsSlider.Value);
        SharpnessValue.Text = Format(SharpnessSlider.Value);
    }

    private ColorGradeParams ReadSliders() => new()
    {
        Brightness = ColorGradeParams.FromSlider(BrightnessSlider.Value),
        Contrast = ColorGradeParams.FromSlider(ContrastSlider.Value),
        Saturation = ColorGradeParams.FromSlider(SaturationSlider.Value),
        Gamma = ColorGradeParams.FromSlider(GammaSlider.Value),
        Hue = ColorGradeParams.FromSlider(HueSlider.Value),
        Shadows = ColorGradeParams.FromSlider(ShadowsSlider.Value),
        Sharpness = ColorGradeParams.FromSlider(SharpnessSlider.Value),
    };

    private void ApplyLive() => _apply(ReadSliders());

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _reset();
        LoadFromSettings();
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        var name = (PresetNameBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(
                this,
                Loc.Get("ColorGrade.SavePreset.NeedName"),
                Loc.Get("ColorGrade.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            PresetNameBox.Focus();
            return;
        }

        try
        {
            var parameters = ReadSliders();
            _apply(parameters);
            ColorGradeStore.Save(name, parameters);
            PresetNameBox.Text = "";
            MessageBox.Show(
                this,
                Loc.Format("ColorGrade.SavePreset.Done", name),
                Loc.Get("ColorGrade.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Saved?.Invoke();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                Loc.Format("ColorGrade.SavePreset.Failed", ex.Message),
                Loc.Get("ColorGrade.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string Format(double v)
    {
        var n = ColorGradeParams.FromSlider(v);
        return n > 0 ? $"+{n}" : n.ToString();
    }
}
