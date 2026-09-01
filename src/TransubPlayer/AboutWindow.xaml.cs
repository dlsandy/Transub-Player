using System.Windows;
using System.Windows.Input;
using System.Windows.Shell;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class AboutWindow : Window
{
    private AboutWindow()
    {
        InitializeComponent();
        WindowChrome.SetWindowChrome(this, WindowChromeUtil.Create(32, canResize: false));
        VersionLabel.Text = Loc.Format("Main.About.Version", AppUpdateService.CurrentVersionText);
    }

    public static void Show(Window owner)
    {
        var win = new AboutWindow { Owner = owner };
        win.ShowDialog();
    }

    private void Caption_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => WindowChromeUtil.DragOrToggle(this, e, allowMaximize: false);

    private void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        // Close About first so an in-place update can shut down MainWindow cleanly.
        var owner = Owner;
        AppSettings? settings = null;
        if (owner is MainWindow main)
            settings = main.SettingsForUpdate;
        Close();
        if (owner is MainWindow mw)
            _ = AppUpdateUi.CheckInteractiveAsync(mw, settings ?? AppSettings.Load(), quietIfCurrent: false);
    }

    private void OpenTransub_Click(object sender, RoutedEventArgs e)
        => FirstRunHelp.OpenTransubSite();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
