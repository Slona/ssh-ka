using Avalonia;
using Avalonia.Media;

namespace SshKa.App.Views;

/// <summary>Доступ к векторным иконкам из Styles/Icons.axaml и кистям темы из кода.</summary>
public static class IconResources
{
    private static object? Find(string key) =>
        Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) ? value : null;

    public static Geometry? Get(string key) => Find(key) as Geometry;

    public static IBrush? Brush(string key) => Find(key) as IBrush;
}
