using System.Globalization;
using System.Windows;

namespace TransubPlayer.Localization;

/// <summary>
/// UI string lookup and culture switch. XAML: <c>{loc:Loc Key}</c> (DynamicResource).
/// Code: <c>Loc.Get("Main.Status.Ready")</c>. Fallback pack is always zh-Hans.
/// </summary>
public static class Loc
{
    private static readonly object Gate = new();
    private static string _appliedTag = UiLanguages.FallbackTag;

    /// <summary>Resolved catalog tag currently loaded (never <c>auto</c>).</summary>
    public static string CurrentTag
    {
        get { lock (Gate) return _appliedTag; }
    }

    public static CultureInfo Culture { get; private set; } = UiLanguages.ToCulture(UiLanguages.FallbackTag);

    /// <summary>Raised on the UI thread after dictionaries / UICulture change.</summary>
    public static event Action? Changed;

    public static void Apply(string? preference)
    {
        var tag = UiLanguages.Resolve(preference);
        var culture = UiLanguages.ToCulture(tag);

        void ApplyCore()
        {
            lock (Gate)
            {
                if (Application.Current is null)
                {
                    _appliedTag = tag;
                    Culture = culture;
                    SetThreadCultures(culture);
                    return;
                }

                var app = Application.Current;
                // Write string keys onto Application.Resources so DynamicResource re-evaluates.
                // Swapping MergedDictionaries alone does not reliably invalidate existing bindings.
                ApplyPackKeys(app.Resources, LoadPack(UiLanguages.FallbackTag));
                if (!tag.Equals(UiLanguages.FallbackTag, StringComparison.OrdinalIgnoreCase))
                    ApplyPackKeys(app.Resources, LoadPack(tag));

                _appliedTag = tag;
                Culture = culture;
                SetThreadCultures(culture);
            }

            Changed?.Invoke();
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            ApplyCore();
        else
            dispatcher.Invoke(ApplyCore);
    }

    public static string Get(string key)
    {
        var resKey = LocExtension.ToResourceKey(key);
        try
        {
            if (Application.Current?.TryFindResource(resKey) is string s && !string.IsNullOrEmpty(s))
                return s;
        }
        catch
        {
            // designer / early init
        }

        return resKey;
    }

    public static string Format(string key, params object?[] args)
    {
        var template = Get(key);
        try
        {
            return string.Format(Culture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    private static void SetThreadCultures(CultureInfo culture)
    {
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        // Keep CurrentCulture for numbers/paths on invariant where callers already do;
        // UICulture alone drives resource selection and message formatting via Loc.Format.
    }

    private static ResourceDictionary LoadPack(string tag) =>
        new()
        {
            Source = new Uri($"pack://application:,,,/Localization/Strings.{tag}.xaml", UriKind.Absolute),
        };

    private static void ApplyPackKeys(ResourceDictionary target, ResourceDictionary pack)
    {
        foreach (var keyObj in pack.Keys)
        {
            if (keyObj is not string key || !key.StartsWith("Str.", StringComparison.Ordinal))
                continue;
            target[key] = pack[key];
        }
    }
}
