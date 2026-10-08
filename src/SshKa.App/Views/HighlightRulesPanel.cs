using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Templates;
using SshKa.App.Highlight;
using SshKa.App.Localization;
using SshKa.App.Storage;

namespace SshKa.App.Views;

/// <summary>Редактор правил подсветки (вкладка окна настроек). Правки применяются методом <see cref="TryApply"/>.</summary>
public sealed class HighlightRulesPanel : UserControl
{
    private readonly ObservableCollection<HighlightRule> _rules;
    private readonly ListBox _list = new() { Width = 230 };
    private readonly TextBox _name = new(), _pattern = new(), _color = new();
    private readonly Border _swatch = new() { Width = 24, Height = 24, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
    private readonly CheckBox _enabled = new() { Content = Loc.T("Включено") };
    private readonly CheckBox _bold = new() { Content = Loc.T("Жирный") };
    private readonly CheckBox _underline = new() { Content = Loc.T("Подчёркнутый") };
    private readonly CheckBox _ignoreCase = new() { Content = Loc.T("Без учёта регистра") };
    private readonly CheckBox _codeInEditors = new()
    {
        Content = Loc.T("Раскрашивать код в редакторах (vim, nano…), если сам редактор этого не делает"),
        IsChecked = AppSettings.Current.HighlightCodeInEditors,
        Margin = new Thickness(0, 6, 0, 0),
    };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
    private bool _loading;

    public HighlightRulesPanel()
    {
        _rules = new ObservableCollection<HighlightRule>(AppSettings.Current.EffectiveHighlightRules.Select(r => r.Clone()));
        _list.ItemsSource = _rules;
        _list.ItemTemplate = new FuncDataTemplate<HighlightRule>((_, _) =>
        {
            var text = new TextBlock();
            text.Bind(TextBlock.TextProperty, new Binding(nameof(HighlightRule.DisplayName)));
            return text;
        });
        _list.SelectionChanged += (_, _) => LoadSelected();

        _name.TextChanged += (_, _) => Edit(r => r.Name = _name.Text ?? "");
        _pattern.TextChanged += (_, _) => Edit(r => r.Pattern = _pattern.Text ?? "");
        _color.TextChanged += (_, _) => Edit(r => r.Color = (_color.Text ?? "").Trim());
        _enabled.IsCheckedChanged += (_, _) => Edit(r => r.Enabled = _enabled.IsChecked == true);
        _bold.IsCheckedChanged += (_, _) => Edit(r => r.Bold = _bold.IsChecked == true);
        _underline.IsCheckedChanged += (_, _) => Edit(r => r.Underline = _underline.IsChecked == true);
        _ignoreCase.IsCheckedChanged += (_, _) => Edit(r => r.IgnoreCase = _ignoreCase.IsChecked == true);

        Content = BuildLayout();
        if (_rules.Count > 0) _list.SelectedIndex = 0;
        LoadSelected();
    }

    private Control BuildLayout()
    {
        Button Small(string text, string tip, Action click)
        {
            var b = new Button { Content = text, MinWidth = 36 };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => click();
            return b;
        }

        var listButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children =
            {
                Small("＋", Loc.T("Добавить правило"), Add),
                Small("✕", Loc.T("Удалить правило"), Remove),
                Small("↑", Loc.T("Выше (важнее при пересечении)"), () => Move(-1)),
                Small("↓", Loc.T("Ниже"), () => Move(1)),
            },
        };

        var left = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        DockPanel.SetDock(listButtons, Dock.Bottom);
        listButtons.Margin = new Thickness(0, 6, 0, 0);
        left.Children.Add(listButtons);
        left.Children.Add(_list);

        var colorRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _color, _swatch } };
        _color.Width = 120;

        var fields = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = Loc.T("Название") }, _name,
                new TextBlock { Text = Loc.T("Регулярное выражение (.NET)") }, _pattern,
                new TextBlock { Text = Loc.T("Цвет текста (#RRGGBB)") }, colorRow,
                _enabled, _bold, _underline, _ignoreCase,
                _codeInEditors,
                new TextBlock
                {
                    Text = Loc.T("Если правила пересекаются, действует то, что выше в списке. " +
                           "Текст, который сервер уже раскрасил сам, и полноэкранные программы (vim, htop) не подсвечиваются."),
                    Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
                },
                _error,
            },
        };

        var reset = new Button { Content = Loc.T("Сбросить по умолчанию"), HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) =>
        {
            _rules.Clear();
            foreach (var r in HighlightRule.Defaults()) _rules.Add(r);
            _list.SelectedIndex = 0;
        };

        var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        bottom.Children.Add(reset);

        var main = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        main.Children.Add(left);
        Grid.SetColumn(fields, 1);
        main.Children.Add(fields);

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(main);
        return root;
    }

    private HighlightRule? Selected => _list.SelectedItem as HighlightRule;

    private void LoadSelected()
    {
        _loading = true;
        var r = Selected;
        var has = r is not null;
        foreach (var c in new Control[] { _name, _pattern, _color, _enabled, _bold, _underline, _ignoreCase }) c.IsEnabled = has;

        _name.Text = r?.Name ?? "";
        _pattern.Text = r?.Pattern ?? "";
        _color.Text = r?.Color ?? "";
        _enabled.IsChecked = r?.Enabled ?? false;
        _bold.IsChecked = r?.Bold ?? false;
        _underline.IsChecked = r?.Underline ?? false;
        _ignoreCase.IsChecked = r?.IgnoreCase ?? false;
        _loading = false;

        UpdateSwatchAndError();
    }

    private void Edit(Action<HighlightRule> apply)
    {
        if (_loading || Selected is not { } r) return;
        apply(r);
        UpdateSwatchAndError();
    }

    private void UpdateSwatchAndError()
    {
        _swatch.Background = Color.TryParse(Selected?.Color, out var c) ? new SolidColorBrush(c) : Brushes.Transparent;
        _error.Text = Selected is { } r ? HighlightEngine.Validate(r) ?? "" : "";
    }

    private void Add()
    {
        var r = new HighlightRule { Name = "Новое правило", Pattern = @"\bслово\b", Color = "#FFA500" };
        _rules.Add(r);
        _list.SelectedItem = r;
    }

    private void Remove()
    {
        if (Selected is not { } r) return;
        var i = _rules.IndexOf(r);
        _rules.Remove(r);
        if (_rules.Count > 0) _list.SelectedIndex = Math.Min(i, _rules.Count - 1);
    }

    private void Move(int delta)
    {
        if (Selected is not { } r) return;
        var i = _rules.IndexOf(r);
        var j = i + delta;
        if (j < 0 || j >= _rules.Count) return;
        _rules.Move(i, j);
        _list.SelectedItem = r;
    }

    /// <summary>Проверяет правила и записывает их в настройки. false — есть ошибка (показана в панели).</summary>
    public bool TryApply()
    {
        foreach (var r in _rules)
        {
            if (HighlightEngine.Validate(r) is { } problem)
            {
                _list.SelectedItem = r;
                _error.Text = $"«{r.Name}»: {problem}";
                return false;
            }
        }

        var settings = AppSettings.Current;
        settings.HighlightRules = _rules.ToList();
        settings.HighlightCodeInEditors = _codeInEditors.IsChecked == true;
        settings.Save();
        HighlightEngine.Reload();
        return true;
    }
}
