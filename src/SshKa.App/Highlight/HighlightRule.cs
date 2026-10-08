using SshKa.App.Localization;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SshKa.App.Highlight;

/// <summary>Правило подсветки: регулярное выражение и оформление найденного текста.</summary>
public partial class HighlightRule : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _name = "";

    [ObservableProperty] private string _pattern = "";

    /// <summary>Цвет текста в виде #RRGGBB.</summary>
    [ObservableProperty] private string _color = "#FFFFFF";

    [ObservableProperty] private bool _bold;
    [ObservableProperty] private bool _underline;
    [ObservableProperty] private bool _ignoreCase = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private bool _enabled = true;

    [JsonIgnore] public string DisplayName => Enabled ? Loc.T(Name) : Loc.T(Name) + Loc.T(" (выкл.)");

    public HighlightRule Clone() => new()
    {
        Name = Name, Pattern = Pattern, Color = Color,
        Bold = Bold, Underline = Underline, IgnoreCase = IgnoreCase, Enabled = Enabled,
    };

    /// <summary>Набор правил по умолчанию; порядок важен: при пересечении побеждает правило выше в списке.</summary>
    public static List<HighlightRule> Defaults() => new()
    {
        // ссылки первыми: слова вроде error или IP внутри адреса не должны перебивать подсветку всей ссылки
        new() { Name = Loc.T("Ссылки"), Color = "#5599FF", Underline = true, IgnoreCase = false,
            Pattern = @"https?://[^\s\x1b]+" },
        new() { Name = Loc.T("Ошибки"), Color = "#FF5555", Bold = true,
            Pattern = @"\b(error|errors|fail|failed|failure|fatal|critical|denied|refused|panic|exception|segfault|unreachable|ошибка|не удалось)\b" },
        new() { Name = Loc.T("Предупреждения"), Color = "#F1C40F",
            Pattern = @"\b(warn|warning|warnings|deprecated|timeout|timed out|предупреждение)\b" },
        new() { Name = Loc.T("Успех"), Color = "#50FA7B",
            Pattern = @"\b(success|successful|successfully|succeeded|passed|ok|готово|успешно)\b" },
        new() { Name = Loc.T("IPv4-адреса"), Color = "#5FD7FF", IgnoreCase = false,
            Pattern = @"\b(?:\d{1,3}\.){3}\d{1,3}(?:/\d{1,2})?\b" },
        new() { Name = Loc.T("Дата и время"), Color = "#BD93F9", IgnoreCase = false,
            Pattern = @"\b\d{4}-\d{2}-\d{2}(?:[T ]\d{2}:\d{2}(?::\d{2})?)?\b|\b\d{2}:\d{2}:\d{2}\b" },
        new() { Name = Loc.T("Уровни логов"), Color = "#6EC1FF", IgnoreCase = false,
            Pattern = @"\b(INFO|DEBUG|TRACE|NOTICE)\b" },
    };
}
