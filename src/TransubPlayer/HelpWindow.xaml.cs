using System.Windows;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class HelpWindow : Window
{
    public enum HelpKind
    {
        Shortcuts,
        SubSource,
    }

    private HelpWindow(HelpKind kind, AppSettings? settings)
    {
        InitializeComponent();
        Title = kind switch
        {
            HelpKind.SubSource => Loc.Get("Help.SubSource.Title"),
            _ => Loc.Get("Help.Shortcuts.Title"),
        };
        TitleLabel.Text = Title;
        BodyLabel.Text = kind switch
        {
            HelpKind.SubSource => Loc.Get("Help.SubSource.Body"),
            HelpKind.Shortcuts when settings is not null && TranslateTargets.IsEnglish(settings)
                => Loc.Get("Help.Shortcuts.Body.EnTarget"),
            _ => Loc.Get("Help.Shortcuts.Body"),
        };
    }

    public static void Show(Window owner, HelpKind kind, AppSettings? settings = null)
    {
        var win = new HelpWindow(kind, settings) { Owner = owner };
        win.ShowDialog();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
