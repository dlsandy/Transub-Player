using System.Windows;
using TransubPlayer.Localization;

namespace TransubPlayer.Services;

internal static class UserFacingErrors
{
    public static string Message(Exception ex)
    {
        return ex switch
        {
            MpvMissingException => Loc.Get("Main.Status.MpvMissing"),
            InvalidOperationException io when io.Message.Contains("尚未打开", StringComparison.Ordinal)
                => Loc.Get("Errors.NoMedia"),
            _ => string.IsNullOrWhiteSpace(ex.Message)
                ? Loc.Get("Errors.Generic")
                : ex.Message,
        };
    }

    public static void Show(Window owner, Exception ex, string title = "Transub Player")
    {
        System.Windows.MessageBox.Show(owner, Message(ex), title,
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
    }
}
