using System.Text.RegularExpressions;

namespace TransubPlayer.Services;

internal enum VadProfile
{
    Soft,
    Film,
    Dialogue,
    DialogueLong,
}

/// <summary>Silent scene knobs from filename (VAD / denoise / profile). Not shown as presets.</summary>
internal sealed record SceneProfile(
    string Language,
    VadProfile Vad,
    bool TimingAlignTen,
    bool Denoise,
    string ContentProfile,
    string? MatchHint = null);

internal static class SceneProfiles
{
    public static SceneProfile Default { get; } = new(
        SourceLanguages.Auto, VadProfile.Dialogue, false, false, "general");

    /// <summary>
    /// Effective ASR language: user override if not auto; else filename match; else auto.
    /// </summary>
    public static string EffectiveLanguage(string? settingsSourceLanguage, SceneProfile? matched)
    {
        var user = SourceLanguages.Normalize(settingsSourceLanguage);
        if (user != SourceLanguages.Auto)
            return user;
        if (matched is not null && !SourceLanguages.IsAuto(matched.Language))
            return SourceLanguages.Normalize(matched.Language);
        return SourceLanguages.Auto;
    }

    public static SceneProfile Resolve(string? settingsSourceLanguage, string? mediaPath, out SceneProfile? matched)
    {
        matched = string.IsNullOrWhiteSpace(mediaPath) ? null : Match(mediaPath);
        var scene = matched ?? Default;
        var lang = EffectiveLanguage(settingsSourceLanguage, matched);
        // Keep scene VAD/profile; override language from settings when locked.
        return scene with { Language = lang };
    }

    public static string PickAsr(string? preferredAsrModel, RuntimePacks packs, string? sourceLanguage = null)
        => AsrQualities.PickAsr(preferredAsrModel, packs, sourceLanguage);

    public static bool IsEnglishSource(string? language)
        => string.Equals(SourceLanguages.Normalize(language), SourceLanguages.En, StringComparison.Ordinal);

    public static SceneProfile? Match(string mediaPath)
    {
        var file = Path.GetFileNameWithoutExtension(mediaPath) ?? "";
        var dir = Path.GetFileName(Path.GetDirectoryName(mediaPath) ?? "") ?? "";
        var hay = file + " " + dir + " " + mediaPath;

        if (ContainsAny(hay, SoftKeys))
            return P(SourceLanguages.Ja, VadProfile.Soft, true, false, "av_soft", "ja-soft");
        if (ContainsAny(hay, GalKeys))
            return P(SourceLanguages.Ja, VadProfile.Soft, true, false, "av_soft", "game-gal");
        if (ContainsAny(hay, AnimeKeys))
            return P(SourceLanguages.Ja, VadProfile.Soft, true, false, "anime", "ja-anime");
        if (ContainsAny(hay, KVarietyKeys))
            return P(SourceLanguages.Ko, VadProfile.Dialogue, false, true, "dialogue", "k-variety");
        if (ContainsAny(hay, KDramaKeys) || HasHangul(hay))
            return P(SourceLanguages.Ko, VadProfile.Film, false, true, "film", "k-drama");
        if (ContainsAny(hay, JaDramaKeys))
            return P(SourceLanguages.Ja, VadProfile.Film, false, true, "film", "ja-drama");
        if (ContainsAny(hay, JaFilmKeys))
            return P(SourceLanguages.Ja, VadProfile.Film, false, true, "film", "ja-film");
        if (ContainsAny(hay, ZhVarietyKeys))
            return P(SourceLanguages.Zh, VadProfile.Dialogue, false, true, "dialogue", "zh-variety");
        if (ContainsAny(hay, ZhDramaKeys))
            return P(SourceLanguages.Zh, VadProfile.Film, false, true, "film", "zh-drama");
        if (ContainsAny(hay, LectureKeys))
            return P(SourceLanguages.Auto, VadProfile.DialogueLong, false, false, "dialogue", "lecture");
        if (ContainsAny(hay, PodcastKeys))
            return P(SourceLanguages.Auto, VadProfile.Dialogue, false, false, "dialogue", "podcast");
        if (ContainsAny(hay, EnSeriesKeys) || SeasonEpisode.IsMatch(hay))
            return P(SourceLanguages.En, VadProfile.Film, false, true, "film", "en-series");
        if (ContainsAny(hay, EnFilmKeys))
            return P(SourceLanguages.En, VadProfile.Film, false, true, "film", "en-film");
        if (HasKana(hay))
            return P(SourceLanguages.Ja, VadProfile.Film, false, true, "film", "ja-drama");
        return null;
    }

    private static SceneProfile P(
        string language, VadProfile vad, bool ten, bool denoise, string profile, string hint)
        => new(language, vad, ten, denoise, profile, hint);

    private static readonly string[] SoftKeys =
    [
        "fc2", "heyzo", "1pondo", "caribbean", "カリビアン", "无码", "無碼", "有码", "有碼",
        "素人", "软声", "軟声", "ssis-", "midv-", "stars-", "sone-", "pred-", "ipzz-", "ipx-",
        "fthtd-", "mida-", "juq-", "same-", "cawd-", "ssni-", "adn-", "stk-",
    ];
    private static readonly string[] GalKeys = ["galgame", "ギャルゲー", "eroge", "エロゲ", "同人音声"];
    private static readonly string[] AnimeKeys = ["anime", "アニメ", "番剧", "番劇", "ova", "oad", "剧场版", "劇場版"];
    private static readonly string[] KVarietyKeys = ["韩综", "韓綜"];
    private static readonly string[] KDramaKeys = ["韩剧", "韓劇", "kdrama", "k-drama", "한국"];
    private static readonly string[] JaDramaKeys = ["日剧", "日劇", "jdrama", "j-drama"];
    private static readonly string[] JaFilmKeys = ["日本映画", "日语电影", "日語電影"];
    private static readonly string[] ZhVarietyKeys = ["综艺", "綜藝"];
    private static readonly string[] ZhDramaKeys = ["国产", "華語", "华语", "港剧", "港劇", "台剧", "台劇", "大陆剧", "大陸劇"];
    private static readonly string[] LectureKeys = ["ted", "lecture", "documentary", "纪录片", "紀錄片", "讲座", "講座"];
    private static readonly string[] PodcastKeys = ["podcast", "播客"];
    private static readonly string[] EnSeriesKeys = ["美剧", "美劇", "英剧", "英劇"];
    private static readonly string[] EnFilmKeys = ["欧美电影", "歐美電影"];
    private static readonly Regex SeasonEpisode = new(@"\bS\d{1,2}E\d{1,3}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool ContainsAny(string hay, IEnumerable<string> keys)
        => keys.Any(k => hay.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static bool HasHangul(string text) => text.Any(c => c is >= '\uAC00' and <= '\uD7A3');
    private static bool HasKana(string text) => text.Any(c => c is >= '\u3040' and <= '\u30FF');
}
