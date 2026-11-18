using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TransubPlayer.Services;

namespace TransubPlayer.Desktop;

internal partial class FavoritesWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly Func<string, Task> _open;
    private IReadOnlyList<LiveFavoriteEntry> _items = [];

    public FavoritesWindow(Func<string, Task> open)
    {
        _open = open;
        InitializeComponent();
        _items = LiveFavorites.All(_settings).ToList();
        List.ItemsSource = _items.Select(i => LiveFavorites.DisplayLabel(i) + " — " + i.Url).ToList();
    }

    private async void OnOpen(object? sender, RoutedEventArgs e)
    {
        if (List.SelectedIndex < 0 || List.SelectedIndex >= _items.Count) return;
        await _open(_items[List.SelectedIndex].Url);
        Close();
    }

    private async void OnDoubleTap(object? sender, TappedEventArgs e) => OnOpen(sender, e);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
