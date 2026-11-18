using System.Windows;
using System.Windows.Input;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class SubtitleCatPickerWindow : Window
{
    internal SubtitleCatResult? Picked { get; private set; }

    private readonly MediaSearchQuery _query;
    private readonly bool _searchOnOpen;
    private readonly Dictionary<OnlineSubtitleProvider, IReadOnlyList<SubtitleCatResult>> _cache = new();
    private readonly Dictionary<OnlineSubtitleProvider, Task<(IReadOnlyList<SubtitleCatResult> Results, string? Note)>> _inflight = new();
    private OnlineSubtitleProvider _provider;
    private bool _ready;
    private bool _initialSearchStarted;
    private CancellationTokenSource? _searchCts;

    private SubtitleCatPickerWindow(SubtitleCatPickRequest request)
    {
        InitializeComponent();
        _query = request.Query;
        _provider = request.InitialProvider;
        _searchOnOpen = request.SearchOnOpen;
        QueryLabel.Text = Loc.Format("SubtitleCat.Picker.Query", request.Query.Primary);

        ApplyProviderRadios();

        if (_searchOnOpen)
        {
            BindResults([]);
            EmptyLabel.Visibility = Visibility.Collapsed;
            ShowSearching();
            _ready = true;
            return;
        }

        _cache[_provider] = request.Results;
        if (!string.IsNullOrWhiteSpace(request.Note))
        {
            NoteLabel.Text = request.Note!;
            NoteLabel.Visibility = Visibility.Visible;
        }
        else if (_provider == OnlineSubtitleProvider.Xunlei
                 && request.Results.Count > 0
                 && request.Results.All(r => r.Source.Equals("迅雷", StringComparison.OrdinalIgnoreCase)))
        {
            NoteLabel.Text = Loc.Get("SubtitleCat.Picker.FallbackNote");
            NoteLabel.Visibility = Visibility.Visible;
        }

        BindResults(request.Results);
        _ready = true;
    }

    internal static SubtitleCatResult? Show(Window owner, SubtitleCatPickRequest request)
    {
        var dlg = new SubtitleCatPickerWindow(request) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Picked : null;
    }

    private async void Window_ContentRendered(object sender, EventArgs e)
    {
        if (!_searchOnOpen || _initialSearchStarted)
            return;
        _initialSearchStarted = true;
        // Kick both sources in parallel so switching never waits on a cold start.
        _ = EnsureSearchStarted(OnlineSubtitleProvider.SubtitleCat);
        _ = EnsureSearchStarted(OnlineSubtitleProvider.Xunlei);
        await ShowProviderAsync(_provider).ConfigureAwait(true);
    }

    private void ApplyProviderRadios()
    {
        ProviderCatRadio.IsChecked = _provider == OnlineSubtitleProvider.SubtitleCat;
        ProviderXunleiRadio.IsChecked = _provider == OnlineSubtitleProvider.Xunlei;
    }

    private void BindResults(IReadOnlyList<SubtitleCatResult> results)
    {
        ResultList.ItemsSource = results;
        if (results.Count > 0)
        {
            ResultList.SelectedIndex = 0;
            EmptyLabel.Visibility = Visibility.Collapsed;
        }
        else
        {
            ResultList.SelectedIndex = -1;
            EmptyLabel.Visibility = Visibility.Visible;
        }
    }

    private void ShowSearching()
    {
        NoteLabel.Text = Loc.Get("SubtitleCat.Picker.Provider.Searching");
        NoteLabel.Visibility = Visibility.Visible;
        SearchProgress.Visibility = Visibility.Visible;
        EmptyLabel.Visibility = Visibility.Collapsed;
        ResultList.ItemsSource = null;
    }

    private async void Provider_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;

        var next = ProviderXunleiRadio.IsChecked == true
            ? OnlineSubtitleProvider.Xunlei
            : OnlineSubtitleProvider.SubtitleCat;
        if (next == _provider) return;

        _provider = next;
        await ShowProviderAsync(next).ConfigureAwait(true);
    }

    private Task<(IReadOnlyList<SubtitleCatResult> Results, string? Note)> EnsureSearchStarted(
        OnlineSubtitleProvider provider)
    {
        if (_cache.TryGetValue(provider, out var cached))
            return Task.FromResult<(IReadOnlyList<SubtitleCatResult>, string?)>((cached, null));

        if (_inflight.TryGetValue(provider, out var existing))
            return existing;

        _searchCts ??= new CancellationTokenSource();
        var ct = _searchCts.Token;
        var task = SearchAndCacheAsync(provider, ct);
        _inflight[provider] = task;
        return task;
    }

    private async Task<(IReadOnlyList<SubtitleCatResult> Results, string? Note)> SearchAndCacheAsync(
        OnlineSubtitleProvider provider,
        CancellationToken ct)
    {
        try
        {
            var results = await SubtitleCatClient.SearchProviderAsync(provider, _query, ct)
                .ConfigureAwait(false);
            _cache[provider] = results;
            return (results, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _cache[provider] = [];
            return ([], Loc.Format("SubtitleCat.Failed", ex.Message));
        }
        finally
        {
            _inflight.Remove(provider);
        }
    }

    private async Task ShowProviderAsync(OnlineSubtitleProvider provider)
    {
        if (_cache.TryGetValue(provider, out var cached))
        {
            SearchProgress.Visibility = Visibility.Collapsed;
            BindResults(cached);
            if (cached.Count == 0)
            {
                NoteLabel.Text = Loc.Get("SubtitleCat.NoResults");
                NoteLabel.Visibility = Visibility.Visible;
            }
            else
            {
                NoteLabel.Visibility = Visibility.Collapsed;
            }
            SetUiBusy(false);
            return;
        }

        ShowSearching();
        SetUiBusy(false); // keep radios enabled while the other source fills cache

        try
        {
            var (results, note) = await EnsureSearchStarted(provider).ConfigureAwait(true);
            if (provider != _provider) return;

            _cache[provider] = results;
            BindResults(results);
            if (results.Count == 0)
            {
                NoteLabel.Text = string.IsNullOrWhiteSpace(note)
                    ? Loc.Get("SubtitleCat.NoResults")
                    : note!;
                NoteLabel.Visibility = Visibility.Visible;
            }
            else if (!string.IsNullOrWhiteSpace(note))
            {
                NoteLabel.Text = note!;
                NoteLabel.Visibility = Visibility.Visible;
            }
            else
            {
                NoteLabel.Visibility = Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException)
        {
            // closed
        }
        finally
        {
            if (provider == _provider)
                SearchProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void SetUiBusy(bool busy)
    {
        // Radios stay enabled so the user can switch sources while a search runs.
        ProviderCatRadio.IsEnabled = true;
        ProviderXunleiRadio.IsEnabled = true;
        UseButton.IsEnabled = !busy;
        ResultList.IsEnabled = !busy;
        // Skip stays enabled so the user can cancel a long search.
    }

    private void Use_Click(object sender, RoutedEventArgs e)
        => ConfirmSelection();

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        CancelSearch();
        Picked = null;
        DialogResult = false;
        Close();
    }

    private void ResultList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => ConfirmSelection();

    private void ConfirmSelection()
    {
        if (ResultList.SelectedItem is not SubtitleCatResult pick)
        {
            MessageBox.Show(this, Loc.Get("SubtitleCat.Picker.NeedSelect"), Loc.Get("SubtitleCat.Picker.Title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        CancelSearch();
        Picked = pick;
        DialogResult = true;
        Close();
    }

    private void CancelSearch()
    {
        try { _searchCts?.Cancel(); } catch { /* ignore */ }
        _searchCts?.Dispose();
        _searchCts = null;
        _inflight.Clear();
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelSearch();
        base.OnClosed(e);
    }
}
