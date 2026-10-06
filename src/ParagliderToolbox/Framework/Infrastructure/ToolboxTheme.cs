using Atelier.Core.Primitives;
using Atelier.Core.Properties;
using Atelier.Core.Tree;
using Atelier.Theming;
using Atelier.Theming.Material;

namespace ParagliderToolbox.Framework.Infrastructure;

/// <summary>The application's light and dark Material themes, and colors from the current one.</summary>
public static class ToolboxTheme
{
    /// <summary>Gets whether the dark theme is shown.</summary>
    public static bool IsDark => ThemeManager.HasTheme && ThemeManager.Current.IsDark;

    /// <summary>Gets the color scheme of the current theme.</summary>
    public static MaterialColorScheme Colors =>
        ThemeManager.HasTheme && ThemeManager.Current is MaterialTheme material ? material.Colors : MaterialColorScheme.Light();

    /// <summary>Shows the dark or the light theme in every window.</summary>
    public static void Apply(bool dark) => ThemeManager.Current = dark ? MaterialTheme.CreateDark() : MaterialTheme.CreateLight();

    /// <summary>
    /// Sets <paramref name="property"/> of <paramref name="element"/> to a color of the current scheme, and again whenever
    /// the theme changes while the element is shown.
    /// </summary>
    public static T Themed<T>(this T element, BindableProperty<Color> property, Func<MaterialColorScheme, Color> pick) where T : UIElement
    {
        void Apply() => element.SetValue(property, pick(Colors));
        void OnThemeChanged(Theme theme) => Apply();

        Apply();
        element.AttachedToVisualTree += (_, _) =>
        {
            ThemeManager.ThemeChanged += OnThemeChanged;
            Apply();
        };
        element.DetachedFromVisualTree += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
        return element;
    }
}
