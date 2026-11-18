using System.Windows;

namespace TransubPlayer.Localization;

/// <summary>WPF bridge: mirrors Core <see cref="Loc"/> packs into Application.Resources for DynamicResource.</summary>
internal static class LocWpf
{
    public static void Hook()
    {
        Loc.AfterPackApplied = (_, merged) =>
        {
            var app = Application.Current;
            if (app is null) return;
            foreach (var kv in merged)
                app.Resources[kv.Key] = kv.Value;
        };
    }
}
