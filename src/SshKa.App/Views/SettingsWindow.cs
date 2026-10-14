using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SshKa.App.Highlight;
using SshKa.App.Localization;
using SshKa.App.Storage;

namespace SshKa.App.Views;

/// <summary>Окно настроек: язык, терминал и правила подсветки. Значения записываются по «OK».</summary>
public sealed class SettingsWindow : Window
{
    private readonly ComboBox _language = new() { MinWidth = 180 };
    private readonly TextBox _fontFamily = new() { MinWidth = 340 };
    private readonly NumericUpDown _fontSize = new() { Minimum = 8, Maximum = 32, Increment = 1, FormatString = "0", MinWidth = 110 };
    private readonly CheckBox _bold = new() { Content = Loc.T("Жирное начертание (иначе «жирный» от сервера показывается яркими цветами)") };
    private readonly CheckBox _copyOnSelect = new() { Content = Loc.T("Копировать выделенный в терминале текст в буфер обмена") };
    private readonly CheckBox _highlight = new() { Content = Loc.T("Подсвечивать вывод по правилам") };
    private readonly HighlightRulesPanel _rules = new();

    private readonly SessionVault? _vault;
    private readonly CheckBox _askPassword = new() { Content = Loc.T("Спрашивать мастер-пароль при каждом запуске") };

    public SettingsWindow(SessionVault? vault = null)
    {
        _vault = vault;
        Title = Loc.T("Настройки");
        Width = 860;
        Height = 640;
        MinWidth = 700;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var settings = AppSettings.Current;
        _language.Items.Add(new ComboBoxItem { Content = "Русский", Tag = "ru" });
        _language.Items.Add(new ComboBoxItem { Content = "English", Tag = "en" });
        _language.SelectedIndex = settings.Language == "en" ? 1 : 0;

        _fontFamily.Text = settings.TerminalFontFamily;
        _fontSize.Value = (decimal)settings.TerminalFontSize;
        _bold.IsChecked = settings.TerminalBold;
        _copyOnSelect.IsChecked = settings.CopyOnSelect;
        _highlight.IsChecked = settings.HighlightEnabled;
        _askPassword.IsChecked = settings.AskMasterPassword;

        var tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = Loc.T("Общие"), Content = GeneralPage() },
                new TabItem { Header = Loc.T("Терминал"), Content = TerminalPage() },
                new TabItem { Header = Loc.T("Подсветка"), Content = HighlightPage() },
                new TabItem { Header = Loc.T("Безопасность"), Content = SecurityPage() },
            },
        };

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = Loc.T("Отмена"), IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => { if (Apply()) Close(true); };
        cancel.Click += (_, _) => Close(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { ok, cancel },
        };

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(tabs);
        Content = root;
    }

    private static StackPanel Page(params Control[] children)
    {
        var page = new StackPanel { Spacing = 10, Margin = new Thickness(14) };
        page.Children.AddRange(children);
        return page;
    }

    private static TextBlock Label(string text) => new() { Text = Loc.T(text) };

    private static TextBlock Hint(string text) => new() { Text = Loc.T(text), Opacity = 0.65, TextWrapping = TextWrapping.Wrap };

    private Control SecurityPage()
    {
        var change = new Button { Content = Loc.T("Сменить мастер-пароль…"), IsEnabled = _vault is not null, HorizontalAlignment = HorizontalAlignment.Left };
        var check = new Button { Content = Loc.T("Проверить мастер-пароль…"), IsEnabled = _vault is not null, HorizontalAlignment = HorizontalAlignment.Left };
        change.Click += async (_, _) => await ChangePasswordAsync();
        check.Click += async (_, _) => await CheckPasswordAsync();

        _askPassword.IsEnabled = VaultKeyCache.IsSupported;
        return Page(
            Hint("Мастер-пароль шифрует файл с сессиями и паролями. По умолчанию после первого ввода программа запоминает ключ на этом компьютере " +
                 "(он защищён Windows и привязан к вашей учётной записи), поэтому при запуске пароль не спрашивается."),
            _askPassword,
            Hint("Если включить, ключ не запоминается и пароль нужен при каждом запуске. Это надёжнее, если к вашей учётной записи Windows имеют доступ другие люди."),
            change,
            check);
    }

    private async Task ChangePasswordAsync()
    {
        if (_vault is null) return;
        var changed = await Dialogs.ChangePassword(this, (current, next) => Task.Run<string?>(() =>
        {
            try
            {
                _vault.ChangePassword(current, next);
                return null;
            }
            catch (CryptographicException) { return "Неверный мастер-пароль"; }
            catch (Exception ex) { return ex.Message; }
        }));
        if (!changed) return;

        // запомненный ключ устарел: обновляем его или удаляем, если пароль нужен при каждом запуске
        if (_askPassword.IsChecked == true) VaultKeyCache.Delete();
        else VaultKeyCache.Save(_vault.ExportKey());
        await Dialogs.Info(this, "Смена мастер-пароля", "Мастер-пароль изменён.");
    }

    private async Task CheckPasswordAsync()
    {
        if (_vault is null) return;
        var entered = await Dialogs.AskText(this, "Проверка мастер-пароля", "Введите мастер-пароль", password: true);
        if (entered is null) return;
        var ok = await Task.Run(() => _vault.VerifyPassword(entered));
        await Dialogs.Info(this, "Проверка мастер-пароля", ok ? "Пароль верный." : "Пароль неверный.");
    }

    private Control GeneralPage() => Page(
        Label("Язык интерфейса"),
        _language,
        Hint("Язык сменится после перезапуска программы."));

    private Control TerminalPage() => Page(
        Label("Шрифт терминала (список через запятую, берётся первый установленный)"),
        _fontFamily,
        Label("Размер шрифта"),
        _fontSize,
        _bold,
        _copyOnSelect);

    private Control HighlightPage()
    {
        var root = new DockPanel();
        DockPanel.SetDock(_highlight, Dock.Top);
        _highlight.Margin = new Thickness(14, 12, 14, 0);
        root.Children.Add(_highlight);
        root.Children.Add(_rules);
        return root;
    }

    private bool Apply()
    {
        if (!_rules.TryApply()) return false; // ошибка в правиле: остаёмся в окне

        var settings = AppSettings.Current;
        settings.Language = (_language.SelectedItem as ComboBoxItem)?.Tag as string ?? "ru";
        if (!string.IsNullOrWhiteSpace(_fontFamily.Text)) settings.TerminalFontFamily = _fontFamily.Text.Trim();
        settings.TerminalFontSize = (double)(_fontSize.Value ?? 14);
        settings.TerminalBold = _bold.IsChecked == true;
        settings.CopyOnSelect = _copyOnSelect.IsChecked == true;
        settings.HighlightEnabled = _highlight.IsChecked == true;
        settings.AskMasterPassword = _askPassword.IsChecked == true;
        settings.Save();
        // выбранный режим: запоминать ключ на этом компьютере или спрашивать пароль при запуске
        if (settings.AskMasterPassword) VaultKeyCache.Delete();
        else if (_vault is not null) VaultKeyCache.Save(_vault.ExportKey());
        HighlightEngine.Reload();
        return true;
    }
}
