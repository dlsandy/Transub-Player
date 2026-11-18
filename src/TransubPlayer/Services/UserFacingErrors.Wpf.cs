using System.Windows;
using TransubPlayer.Services;

namespace TransubPlayer.Services;

internal static class UserFacingErrorsUi
{
    public static void Show(Window owner, Exception ex, string title = "Transub Player")
    {
        MessageBox.Show(owner, UserFacingErrors.Message(ex), title,
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
