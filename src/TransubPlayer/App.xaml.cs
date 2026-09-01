using System.Windows;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class App : System.Windows.Application
{
    private static int _unhandledDialogOpen;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            // MessageBox pumps the dispatcher; a recurring XAML fault would otherwise
            // open unbounded error dialogs.
            if (Interlocked.CompareExchange(ref _unhandledDialogOpen, 1, 0) != 0)
                return;
            try
            {
                PlayerLog.Write("UI 未处理异常：" + args.Exception);
                MessageBox.Show(args.Exception.Message, "Transub Player", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Interlocked.Exchange(ref _unhandledDialogOpen, 0);
            }
        };
        SessionEnding += (_, _) => ChildProcessLifetime.KillRemaining();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ChildProcessLifetime.KillRemaining();

        // Zip extracts keep Zone.Identifier; Windows Defender / SmartScreen may block exe + mpv + python.
        try
        {
            var root = AppContext.BaseDirectory;
            var cleared = MarkOfTheWeb.ClearLaunchCritical(root);
            if (cleared > 0)
                PlayerLog.Write($"Startup: cleared Internet mark on {cleared} launch file(s)");
            MarkOfTheWeb.ClearInstallDirectoryBackground(root);
        }
        catch
        {
            // ignore — must not block launch
        }

        // Before MainWindow loads so first paint uses the right pack.
        Loc.Apply(AppSettings.Load().UiLanguage);

        if (!SingleInstanceHost.TryBecomePrimary(out var startupArgs))
        {
            Shutdown();
            return;
        }

        var main = new MainWindow();
        MainWindow = main;
        // Listen before Show so a rapid second launch can forward paths.
        SingleInstanceHost.StartListening(main.HandleExternalOpen);
        main.Show();
        if (startupArgs.Length > 0)
            main.HandleExternalOpen(startupArgs);

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SingleInstanceHost.StopListening();
        ChildProcessLifetime.KillRemaining();
        base.OnExit(e);
    }
}
