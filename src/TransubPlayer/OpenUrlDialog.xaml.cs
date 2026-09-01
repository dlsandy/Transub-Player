using System.Windows;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class OpenUrlDialog : Window
{
    public string? Url { get; private set; }
    public bool AddToFavorites { get; private set; }

    public OpenUrlDialog(string? initial = null)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(initial))
            UrlBox.Text = initial.Trim();
        UrlBox.SelectAll();
        UrlBox.Focus();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!MediaSourceHelper.TryNormalizeMedia(UrlBox.Text, out var url))
        {
            MessageBox.Show(this, Loc.Get("OpenUrl.Invalid"), Loc.Get("OpenUrl.Title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Url = url;
        AddToFavorites = AddFavoriteBox.IsChecked == true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}
