using System.Windows;
using System.Windows.Controls;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class MainWindow
{
    private void RefreshFavoritesMenu()
    {
        FavoritesMenu.Items.Clear();
        var items = LiveFavorites.All(_settings).ToList();
        if (items.Count == 0)
        {
            FavoritesMenu.Items.Add(new MenuItem { Header = Loc.Get("Main.FavoritesEmpty"), IsEnabled = false });
        }
        else
        {
            foreach (var entry in items)
            {
                var url = entry.Url;
                var parent = new MenuItem
                {
                    Header = LiveFavorites.DisplayLabel(entry),
                    ToolTip = url,
                };

                var open = new MenuItem { Header = Loc.Get("Main.Favorites.Open") };
                open.Click += async (_, _) => await OpenPathAsync(url);
                parent.Items.Add(open);

                var rename = new MenuItem { Header = Loc.Get("Main.Favorites.Rename") };
                rename.Click += (_, _) => RenameFavorite(url);
                parent.Items.Add(rename);

                var remove = new MenuItem { Header = Loc.Get("Main.Favorites.Remove") };
                remove.Click += (_, _) => RemoveFavorite(url);
                parent.Items.Add(remove);

                FavoritesMenu.Items.Add(parent);
            }
        }

        FavoritesMenu.Items.Add(new Separator());

        var addCurrent = new MenuItem
        {
            Header = Loc.Get("Main.Favorites.AddCurrent"),
            IsEnabled = CanFavoriteCurrent(),
            Tag = "add-current",
        };
        addCurrent.Click += (_, _) => AddCurrentFavorite();
        FavoritesMenu.Items.Add(addCurrent);

        var clear = new MenuItem
        {
            Header = Loc.Get("Main.Favorites.Clear"),
            IsEnabled = items.Count > 0,
        };
        clear.Click += ClearFavorites_Click;
        FavoritesMenu.Items.Add(clear);
    }

    private void UpdateFavoriteCurrentEnabled()
    {
        foreach (var obj in FavoritesMenu.Items)
        {
            if (obj is MenuItem { Tag: "add-current" } mi)
            {
                mi.IsEnabled = CanFavoriteCurrent();
                break;
            }
        }
    }

    private bool CanFavoriteCurrent()
    {
        var path = _preview?.MediaPath;
        return !string.IsNullOrWhiteSpace(path)
               && MediaSourceHelper.IsRemoteUrl(path)
               && !MediaSourceHelper.IsScreenCapture(path);
    }

    private void AddCurrentFavorite()
    {
        var path = _preview?.MediaPath;
        if (string.IsNullOrWhiteSpace(path) || !LiveFavorites.TryNormalizeFavoriteUrl(path, out var url))
        {
            SetStatus(Loc.Get("Main.Status.FavoriteUnavailable"));
            return;
        }

        var defaultName = LiveFavorites.Contains(_settings, url)
            ? LiveFavorites.DisplayLabel(_settings.LiveFavorites.First(e =>
                string.Equals(e.Url, url, StringComparison.OrdinalIgnoreCase)))
            : MediaSourceHelper.DisplayName(url);

        var dlg = new FavoriteNameDialog(url, defaultName) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        LiveFavorites.Add(_settings, url, dlg.FavoriteName);
        _settings.Save();
        RefreshFavoritesMenu();
        SetStatus(Loc.Get("Main.Status.FavoriteAdded"));
    }

    private void RenameFavorite(string url)
    {
        if (!LiveFavorites.TryNormalizeFavoriteUrl(url, out var normalized)) return;
        var entry = _settings.LiveFavorites.FirstOrDefault(e =>
            string.Equals(e.Url, normalized, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return;

        var dlg = new FavoriteNameDialog(normalized, LiveFavorites.DisplayLabel(entry))
        {
            Owner = this,
            Title = Loc.Get("FavoriteName.RenameTitle"),
        };
        if (dlg.ShowDialog() != true) return;

        LiveFavorites.Rename(_settings, normalized, dlg.FavoriteName);
        _settings.Save();
        RefreshFavoritesMenu();
        SetStatus(Loc.Get("Main.Status.FavoriteRenamed"));
    }

    private void RemoveFavorite(string url)
    {
        if (!LiveFavorites.Remove(_settings, url)) return;
        _settings.Save();
        RefreshFavoritesMenu();
        SetStatus(Loc.Get("Main.Status.FavoriteRemoved"));
    }

    private void ClearFavorites_Click(object sender, RoutedEventArgs e)
    {
        LiveFavorites.Clear(_settings);
        _settings.Save();
        RefreshFavoritesMenu();
        SetStatus(Loc.Get("Main.Status.FavoriteCleared"));
    }

    private void TryAddFavoriteFromOpenUrl(string url)
    {
        if (!LiveFavorites.TryNormalizeFavoriteUrl(url, out var normalized)) return;
        var already = LiveFavorites.Contains(_settings, normalized);
        LiveFavorites.Add(_settings, normalized, MediaSourceHelper.DisplayName(normalized));
        _settings.Save();
        RefreshFavoritesMenu();
        SetStatus(Loc.Get(already ? "Main.Status.FavoriteAlready" : "Main.Status.FavoriteAdded"));
    }
}
