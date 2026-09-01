using System.Windows;
using TransubPlayer.Localization;

namespace TransubPlayer;

public partial class FavoriteNameDialog : Window
{
    public string FavoriteName { get; private set; } = "";

    public FavoriteNameDialog(string url, string? initialName = null)
    {
        InitializeComponent();
        UrlHint.Text = url;
        NameBox.Text = string.IsNullOrWhiteSpace(initialName) ? "" : initialName.Trim();
        NameBox.SelectAll();
        NameBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        FavoriteName = NameBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(FavoriteName))
        {
            MessageBox.Show(this, Loc.Get("FavoriteName.NameRequired"), Loc.Get("FavoriteName.Title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}
