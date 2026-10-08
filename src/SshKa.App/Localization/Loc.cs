using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using SshKa.App.Storage;

namespace SshKa.App.Localization;

/// <summary>
/// Перевод интерфейса. Ключом служит русский текст: он же и показывается, если перевода нет или выбран русский.
/// Язык применяется при запуске (смена в настройках действует после перезапуска).
/// </summary>
public static class Loc
{
    private static readonly bool English = AppSettings.Current.Language == "en";

    public static bool IsEnglish => English;

    public static string T(string ru) => English && Strings.En.TryGetValue(ru, out var en) ? en : ru;

    public static string F(string ru, params object?[] args) => string.Format(T(ru), args);

    /// <summary>Переводит тексты уже построенных элементов (разметка XAML): подписи, заголовки, подсказки.</summary>
    public static void Apply(ILogical root)
    {
        if (!English) return;
        Walk(root);
        foreach (var d in root.GetLogicalDescendants()) Walk(d);
    }

    private static void Walk(ILogical node)
    {
        switch (node)
        {
            case Window w when w.Title is { Length: > 0 } title:
                w.Title = T(title);
                break;
            case TextBlock tb when tb.Text is { Length: > 0 } text:
                tb.Text = T(text);
                break;
            case HeaderedContentControl hc when hc.Header is string header:
                hc.Header = T(header);
                break;
            case ContentControl cc when cc.Content is string content:
                cc.Content = T(content);
                break;
            case TextBox box when box.PlaceholderText is { Length: > 0 } mark:
                box.PlaceholderText = T(mark);
                break;
            case HeaderedItemsControl hi when hi.Header is string header:
                hi.Header = T(header);
                break;
        }

        if (node is Control control)
        {
            if (ToolTip.GetTip(control) is string tip) ToolTip.SetTip(control, T(tip));
            if (control.ContextMenu is { } menu)
            {
                Walk(menu);
                foreach (var d in menu.GetLogicalDescendants()) Walk(d);
            }
        }
    }
}
