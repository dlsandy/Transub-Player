using System.Globalization;
using System.Reflection;
using System.Xml.Linq;

namespace TransubPlayer.Localization;

/// <summary>
/// UI string lookup shared by WPF and Avalonia shells.
/// Packs are embedded <c>Strings.*.xaml</c> ResourceDictionaries parsed as XML.
/// </summary>
public static class Loc
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Dictionary<string, string>> PackCache = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string> _active = new(StringComparer.Ordinal);
    private static string _appliedTag = UiLanguages.FallbackTag;

    /// <summary>Optional hook so WPF can mirror keys into Application.Resources.</summary>
    public static Action<string, IReadOnlyDictionary<string, string>>? AfterPackApplied { get; set; }

    public static string CurrentTag
    {
        get { lock (Gate) return _appliedTag; }
    }

    public static CultureInfo Culture { get; private set; } = UiLanguages.ToCulture(UiLanguages.FallbackTag);

    public static event Action? Changed;

    public static void Apply(string? preference)
    {
        var tag = UiLanguages.Resolve(preference);
        var culture = UiLanguages.ToCulture(tag);

        lock (Gate)
        {
            var merged = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in LoadPack(UiLanguages.FallbackTag))
                merged[kv.Key] = kv.Value;
            if (!tag.Equals(UiLanguages.FallbackTag, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var kv in LoadPack(tag))
                    merged[kv.Key] = kv.Value;
            }

            _active = merged;
            _appliedTag = tag;
            Culture = culture;
            SetThreadCultures(culture);
            AfterPackApplied?.Invoke(tag, merged);
        }

        Changed?.Invoke();
    }

    public static string Get(string key)
    {
        var resKey = ToResourceKey(key);
        lock (Gate)
        {
            if (_active.Count == 0)
                Apply(UiLanguages.FallbackTag);

            if (_active.TryGetValue(resKey, out var s) && !string.IsNullOrEmpty(s))
                return s;
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

    public static string ToResourceKey(string logicalKey)
    {
        if (string.IsNullOrWhiteSpace(logicalKey))
            return "Str.";
        return logicalKey.StartsWith("Str.", StringComparison.Ordinal)
            ? logicalKey
            : "Str." + logicalKey;
    }

    private static void SetThreadCultures(CultureInfo culture)
    {
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    private static IReadOnlyDictionary<string, string> LoadPack(string tag)
    {
        lock (Gate)
        {
            if (PackCache.TryGetValue(tag, out var cached))
                return cached;
        }

        var map = ReadEmbeddedPack(tag);
        lock (Gate)
            PackCache[tag] = map;
        return map;
    }

    private static Dictionary<string, string> ReadEmbeddedPack(string tag)
    {
        var asm = typeof(Loc).Assembly;
        var name = $"TransubPlayer.Localization.Strings.{tag}.xaml";
        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null)
        {
            // Fallback: try any resource ending with Strings.{tag}.xaml
            var match = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith($"Strings.{tag}.xaml", StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return new Dictionary<string, string>(StringComparer.Ordinal);
            using var s2 = asm.GetManifestResourceStream(match)!;
            return ParseXamlStrings(s2);
        }

        return ParseXamlStrings(stream);
    }

    private static Dictionary<string, string> ParseXamlStrings(Stream stream)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        XDocument doc;
        try
        {
            doc = XDocument.Load(stream);
        }
        catch
        {
            return map;
        }

        XNamespace sys = "clr-namespace:System;assembly=System.Runtime";
        foreach (var el in doc.Descendants())
        {
            if (el.Name.LocalName != "String")
                continue;
            var key = (string?)el.Attribute("Key") ?? (string?)el.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key");
            // x:Key lives in XAML namespace
            key ??= (string?)el.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"));
            if (string.IsNullOrEmpty(key) || !key.StartsWith("Str.", StringComparison.Ordinal))
                continue;
            map[key] = el.Value ?? "";
        }

        return map;
    }
}
