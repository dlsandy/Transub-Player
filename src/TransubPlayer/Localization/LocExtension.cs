using System.Windows;
using System.Windows.Markup;

namespace TransubPlayer.Localization;

/// <summary>
/// XAML: <c>Text="{loc:Loc Main.Tagline}"</c> → DynamicResource <c>Str.Main.Tagline</c>.
/// Updates when <see cref="Loc.Apply"/> swaps dictionaries.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrWhiteSpace(Key))
            return DependencyProperty.UnsetValue;

        // DataTemplate / ControlTemplate parse with an internal SharedDp target (not a
        // DependencyObject). Returning ResourceReferenceExpression there stores it as a
        // plain value; applying the template then sets Text to that object and throws
        // "'ResourceReferenceExpression' is not a valid value for property 'Text'".
        // Return this so ProvideValue runs again when the template is inflated.
        if (serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget pvt
            && pvt.TargetObject is not DependencyObject)
            return this;

        return new DynamicResourceExtension(ToResourceKey(Key)).ProvideValue(serviceProvider);
    }

    public static string ToResourceKey(string key)
    {
        key = key.Trim();
        return key.StartsWith("Str.", StringComparison.Ordinal) ? key : "Str." + key;
    }
}
