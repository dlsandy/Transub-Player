using System.Text.RegularExpressions;

namespace TransubPlayer.Services;

/// <summary>
/// Filename-derived scene hints. Only <see cref="Language"/> and <see cref="ContentProfile"/>
/// affect runtime (ASR language, sanitize/MT route). MatchHint is diagnostics-only.
/// </summary>
internal sealed record SceneProfile(
    string Language,
    string ContentProfile,
    string? MatchHint = null);

internal static class SceneProfiles
{
    public static SceneProfile Default { get; } = new(SourceLanguages.Auto, "general");

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
        // Keep ContentProfile from filename; override language from settings when locked.
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

        // Explicit audio-language tags first (beats genre tags like anime on dubbed rips).
        if (ContainsAny(hay, ZhAudioKeys))
            return P(SourceLanguages.Zh, "film", "zh-audio");
        if (ContainsAny(hay, JaAudioKeys))
            return P(SourceLanguages.Ja, "film", "ja-audio");
        if (ContainsAny(hay, KoAudioKeys))
            return P(SourceLanguages.Ko, "film", "ko-audio");
        if (ContainsAny(hay, EnAudioKeys))
            return P(SourceLanguages.En, "film", "en-audio");
        if (ContainsAny(hay, EsAudioKeys))
            return P(SourceLanguages.Es, "film", "es-audio");

        if (ContainsAny(hay, SoftKeys))
            return P(SourceLanguages.Ja, "av_soft", "ja-soft");
        if (ContainsAny(hay, GalKeys))
            return P(SourceLanguages.Ja, "av_soft", "game-gal");
        if (ContainsAny(hay, AnimeKeys))
            return P(SourceLanguages.Ja, "anime", "ja-anime");
        if (ContainsAny(hay, KVarietyKeys))
            return P(SourceLanguages.Ko, "dialogue", "k-variety");
        if (ContainsAny(hay, KDramaKeys) || HasHangul(hay))
            return P(SourceLanguages.Ko, "film", "k-drama");
        if (ContainsAny(hay, JaDramaKeys))
            return P(SourceLanguages.Ja, "film", "ja-drama");
        if (ContainsAny(hay, JaFilmKeys))
            return P(SourceLanguages.Ja, "film", "ja-film");
        if (ContainsAny(hay, ZhVarietyKeys))
            return P(SourceLanguages.Zh, "dialogue", "zh-variety");
        if (ContainsAny(hay, ZhDramaKeys))
            return P(SourceLanguages.Zh, "film", "zh-drama");
        if (ContainsAny(hay, LectureKeys))
            return P(SourceLanguages.Auto, "dialogue", "lecture");
        if (ContainsAny(hay, PodcastKeys))
            return P(SourceLanguages.Auto, "dialogue", "podcast");
        if (ContainsAny(hay, EsSeriesKeys) || ContainsAny(hay, EsFilmKeys))
            return P(SourceLanguages.Es, "film", "es-film");
        if (ContainsAny(hay, EnSeriesKeys) || SeasonEpisode.IsMatch(hay))
            return P(SourceLanguages.En, "film", "en-series");
        if (ContainsAny(hay, EnFilmKeys))
            return P(SourceLanguages.En, "film", "en-film");
        if (HasKana(hay))
            return P(SourceLanguages.Ja, "film", "ja-kana");
        return null;
    }

    private static SceneProfile P(string language, string profile, string hint)
        => new(language, profile, hint);

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
    private static readonly string[] ZhDramaKeys =
    [
        "国产", "華語", "华语", "港剧", "港劇", "台剧", "台劇", "大陆剧", "大陸劇",
        "国产剧", "國產劇", "华语电影", "華語電影",
    ];
    private static readonly string[] LectureKeys = ["ted", "lecture", "documentary", "纪录片", "紀錄片", "讲座", "講座"];
    private static readonly string[] PodcastKeys = ["podcast", "播客"];
    private static readonly string[] EsSeriesKeys =
    [
        "西剧", "西劇", "西语剧", "西語劇", "西语", "西語", "西班牙语", "西班牙語",
        "拉美剧", "拉美劇", "西班牙剧", "西班牙劇",
    ];
    private static readonly string[] EsFilmKeys =
    [
        "西语电影", "西語電影", "西班牙语电影", "西班牙語電影", "拉美电影", "拉美電影",
        "spanish", "español", "espanol", "latam",
    ];
    private static readonly string[] EnSeriesKeys = ["美剧", "美劇", "英剧", "英劇"];
    private static readonly string[] EnFilmKeys = ["欧美电影", "歐美電影"];

    // Explicit audio / dub tags — preferred over genre keywords.
    // Avoid bare [zh]/[ja] (usually softsub language, not spoken audio).
    private static readonly string[] ZhAudioKeys =
    [
        "国配", "國配", "中配", "普通话", "普通話", "国语配音", "國語配音", "中文配音",
        "粤配", "粵配", "粤语配音", "粵語配音", "中文音轨", "中文音軌", "国语音轨", "國語音軌",
        "audio.zh", "audio-zh", "lang=zh", "language=zh",
    ];
    private static readonly string[] JaAudioKeys =
    [
        "日配", "日语配音", "日語配音", "日语音轨", "日語音軌",
        "audio.ja", "audio-ja", "lang=ja", "language=ja",
    ];
    private static readonly string[] KoAudioKeys =
    [
        "韩配", "韓配", "韩语配音", "韓語配音", "韩语音轨", "韓語音軌",
        "audio.ko", "audio-ko", "lang=ko", "language=ko",
    ];
    private static readonly string[] EnAudioKeys =
    [
        "英配", "英语配音", "英語配音", "英语音轨", "英語音軌",
        "audio.en", "audio-en", "lang=en", "language=en",
    ];
    private static readonly string[] EsAudioKeys =
    [
        "西语配音", "西語配音", "西语音轨", "西語音軌",
        "audio.es", "audio-es", "lang=es", "language=es",
    ];

    private static readonly Regex SeasonEpisode = new(@"\bS\d{1,2}E\d{1,3}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool ContainsAny(string hay, IEnumerable<string> keys)
        => keys.Any(k => hay.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static bool HasHangul(string text) => text.Any(c => c is >= '\uAC00' and <= '\uD7A3');
    private static bool HasKana(string text) => text.Any(c => c is >= '\u3040' and <= '\u30FF');
}
