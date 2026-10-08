using SshKa.App.Localization;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SshKa.App.Storage;

namespace SshKa.App.Highlight;

/// <summary>
/// Применяет скомпилированные правила к куску обычного текста (без escape-последовательностей),
/// оборачивая совпадения в SGR-коды. Набор правил можно менять на лету через Reload.
/// </summary>
public static class HighlightEngine
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    private sealed record Compiled(Regex Regex, string Open, bool Bold, bool Underline);

    private static volatile Compiled[] _rules = Array.Empty<Compiled>();

    public static volatile bool Enabled = true;
    public static volatile bool CodeEnabled = true;

    // цвета токенов кода (палитра в духе One Dark)
    private static readonly Dictionary<string, string> CodeColors = new()
    {
        ["c"] = "\u001b[38;2;127;132;142m",  // комментарий
        ["s"] = "\u001b[38;2;152;195;121m",  // строка
        ["k"] = "\u001b[38;2;198;120;221m",  // ключевое слово
        ["b"] = "\u001b[38;2;86;182;194m",   // встроенное, константа
        ["n"] = "\u001b[38;2;209;154;102m",  // число
        ["v"] = "\u001b[38;2;229;192;123m",  // переменная
    };

    /// <summary>Раскрашивает кусок кода токенизатором языка.</summary>
    public static string ApplyCode(string text, CodeLanguage language)
    {
        if (text.Length < 2) return text;

        MatchCollection matches;
        try { matches = language.Tokenizer.Matches(text); }
        catch (RegexMatchTimeoutException) { return text; }

        var sb = new StringBuilder(text.Length + 32);
        var pos = 0;
        try
        {
            foreach (Match m in matches)
            {
                if (m.Length == 0) continue;
                string? color = null;
                foreach (var kind in CodeColors.Keys)
                    if (m.Groups[kind].Success) { color = CodeColors[kind]; break; }
                if (color is null) continue;

                sb.Append(text, pos, m.Index - pos);
                sb.Append(color).Append(text, m.Index, m.Length).Append("\u001b[39m");
                pos = m.Index + m.Length;
            }
        }
        catch (RegexMatchTimeoutException) { return text; }

        if (pos == 0) return text;
        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }

    public static void Reload()
    {
        var settings = AppSettings.Current;
        Enabled = settings.HighlightEnabled;
        CodeEnabled = settings.HighlightCodeInEditors;
        _rules = Compile(settings.EffectiveHighlightRules);
    }

    /// <summary>Проверяет правило; возвращает текст ошибки или null, если всё в порядке.</summary>
    public static string? Validate(HighlightRule rule)
    {
        if (string.IsNullOrEmpty(rule.Pattern)) return Loc.T("Пустое выражение");
        try { _ = new Regex(rule.Pattern, RegexOptions.None, MatchTimeout); }
        catch (ArgumentException ex) { return Loc.T("Некорректное регулярное выражение: ") + ex.Message; }
        if (!TryParseColor(rule.Color, out _, out _, out _)) return Loc.T("Цвет нужно задать как #RRGGBB");
        return null;
    }

    private static Compiled[] Compile(IEnumerable<HighlightRule> rules)
    {
        var result = new List<Compiled>();
        foreach (var rule in rules.Where(r => r.Enabled))
        {
            if (Validate(rule) is not null) continue; // битое правило не должно ломать остальные
            TryParseColor(rule.Color, out var r, out var g, out var b);

            var options = RegexOptions.CultureInvariant | (rule.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            var open = "\u001b[" + (rule.Bold && SshKa.App.Storage.AppSettings.Current.TerminalBold ? "1;" : "") + (rule.Underline ? "4;" : "") + $"38;2;{r};{g};{b}m";
            result.Add(new Compiled(new Regex(rule.Pattern, options, MatchTimeout), open, rule.Bold, rule.Underline));
        }
        return result.ToArray();
    }

    private static bool TryParseColor(string? text, out int r, out int g, out int b)
    {
        r = g = b = 0;
        if (text is not { Length: 7 } || text[0] != '#') return false;
        return int.TryParse(text.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
            && int.TryParse(text.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
            && int.TryParse(text.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
    }

    /// <param name="serverBold">Сервер уже включил жирный — тогда после совпадения его не выключаем.</param>
    /// <param name="serverUnderline">То же для подчёркивания.</param>
    public static string Apply(string text, bool serverBold, bool serverUnderline)
    {
        var rules = _rules;
        if (rules.Length == 0 || text.Length < 2) return text;

        List<(int Start, int Length, Compiled Rule)>? chosen = null;
        foreach (var rule in rules)
        {
            MatchCollection matches;
            try { matches = rule.Regex.Matches(text); }
            catch (RegexMatchTimeoutException) { continue; } // слишком тяжёлое выражение пропускаем

            try
            {
                foreach (Match m in matches)
                {
                    if (m.Length == 0) continue;
                    chosen ??= new();
                    // при пересечении выигрывает правило, стоящее в списке раньше
                    if (chosen.Any(c => m.Index < c.Start + c.Length && c.Start < m.Index + m.Length)) continue;
                    chosen.Add((m.Index, m.Length, rule));
                }
            }
            catch (RegexMatchTimeoutException) { }
        }
        if (chosen is null or { Count: 0 }) return text;

        chosen.Sort((a, b) => a.Start.CompareTo(b.Start));
        var sb = new StringBuilder(text.Length + chosen.Count * 20);
        var pos = 0;
        foreach (var (start, length, rule) in chosen)
        {
            sb.Append(text, pos, start - pos);
            sb.Append(rule.Open);
            sb.Append(text, start, length);
            sb.Append("\u001b[39m");
            if (rule.Bold && !serverBold) sb.Append("\u001b[22m");
            if (rule.Underline && !serverUnderline) sb.Append("\u001b[24m");
            pos = start + length;
        }
        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }
}
