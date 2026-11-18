using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TransubPlayer.Services;

namespace TransubPlayer.Desktop;

internal partial class PlaylistWindow : Window
{
    private readonly PlaybackQueue _queue;
    private readonly Func<string, Task> _open;

    public PlaylistWindow(PlaybackQueue queue, Func<string, Task> open)
    {
        _queue = queue;
        _open = open;
        InitializeComponent();
        Refresh();
        _queue.Changed += Refresh;
    }

    private void Refresh()
    {
        List.ItemsSource = _queue.Items.Select(Path.GetFileName).ToList();
        if (_queue.Index >= 0 && _queue.Index < _queue.Count)
            List.SelectedIndex = _queue.Index;
    }

    private async void OnPlay(object? sender, RoutedEventArgs e)
    {
        if (List.SelectedIndex < 0) return;
        if (!_queue.TryActivate(List.SelectedIndex)) return;
        if (_queue.Current is { } path)
        {
            await _open(path);
            Close();
        }
    }

    private async void OnDoubleTap(object? sender, TappedEventArgs e) => OnPlay(sender, e);

    private void OnClear(object? sender, RoutedEventArgs e)
    {
        _queue.Replace([]);
        Refresh();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
