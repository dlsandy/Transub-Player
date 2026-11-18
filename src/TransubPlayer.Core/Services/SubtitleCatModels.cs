namespace TransubPlayer.Services;

internal enum OnlineSubtitleProvider
{
    SubtitleCat,
    Xunlei,
}

internal sealed record SubtitleCatResult(
    string Title,
    string DetailPath,
    string Size,
    int Downloads,
    int Score = 0,
    string Source = "SubtitleCat",
    string? DirectUrl = null);

internal sealed record SubtitleCatPickRequest(
    string MediaPath,
    MediaSearchQuery Query,
    IReadOnlyList<SubtitleCatResult> Results,
    OnlineSubtitleProvider InitialProvider = OnlineSubtitleProvider.SubtitleCat,
    string? Note = null,
    bool SearchOnOpen = false);
