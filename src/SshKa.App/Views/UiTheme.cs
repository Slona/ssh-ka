using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;

namespace SshKa.App.Views;

/// <summary>Тема варианта Modern: тёмный Fluent и тёмная палитра на уровне всего приложения —
/// тогда окно, диалоги, меню и таблицы красятся автоматически. Классический вариант не трогает.</summary>
public static class UiTheme
{
    public static readonly Uri ModernResources = new("avares://SshKa.App/Styles/ModernResources.axaml");
    public static readonly Uri ModernTheme = new("avares://SshKa.App/Styles/ModernTheme.axaml");
    private static bool _applied;

    /// <summary>Вызывается один раз до создания окон: вариант Dark и тёмные ресурсы в приложении.</summary>
    public static void ApplyApplication()
    {
        if (!App.ModernUi || _applied) return;
        _applied = true;
        var app = Application.Current!;
        app.RequestedThemeVariant = ThemeVariant.Dark;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(ModernResources) { Source = ModernResources });
    }

    /// <summary>Для отдельных окон (диалоги, настройки): тёмный вариант уже включён на уровне приложения,
    /// а здесь добавляются тёмные стили контролов (вкладки, дерево, таблица) — иначе окна Modern
    /// получают светлые шаблоны Theme.axaml (например, нечитаемые вкладки настроек).</summary>
    public static void Apply(Window window)
    {
        if (!App.ModernUi) return;
        window.Styles.Add(new StyleInclude(ModernTheme) { Source = ModernTheme });
    }

    /// <summary>Кисть из ресурсов приложения с учётом варианта окна.</summary>
    public static Avalonia.Media.IBrush? Brush(Window window, string key)
    {
        var app = Application.Current;
        return app is not null && app.TryGetResource(key, window.ActualThemeVariant, out var value)
            ? value as Avalonia.Media.IBrush
            : null;
    }
}
