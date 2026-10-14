using Avalonia.Controls;
using Avalonia.Layout;
using SshKa.App.Localization;

namespace SshKa.App.Views;

/// <summary>Небольшие модальные диалоги, собранные в коде.</summary>
public static class Dialogs
{
    private static Window Make(string title) => new()
    {
        Title = title,
        SizeToContent = SizeToContent.WidthAndHeight,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        CanResize = false,
        Content = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 10, Children = { } },
    };

    private static StackPanel Buttons(out Button ok, out Button cancel, string okText = "OK")
    {
        ok = new Button { Content = okText, IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { ok, cancel },
        };
    }

    public static async Task<string?> AskText(Window owner, string title, string label, string initial = "", bool password = false)
    {
        var box = new TextBox { Text = initial, MinWidth = 300 };
        if (password) box.PasswordChar = '*';
        var buttons = Buttons(out var ok, out var cancel);
        var win = Make(title);
        ((StackPanel)win.Content!).Children.AddRange(new Control[] { new TextBlock { Text = label }, box, buttons });
        ok.Click += (_, _) => win.Close(box.Text ?? "");
        cancel.Click += (_, _) => win.Close(null);
        win.Opened += (_, _) => box.Focus();
        Loc.Apply(win);
        return await win.ShowDialog<string?>(owner);
    }

    public static async Task<bool> Confirm(Window owner, string title, string message, string okText = "Да")
    {
        var buttons = Buttons(out var ok, out var cancel, okText);
        var win = Make(title);
        ((StackPanel)win.Content!).Children.AddRange(new Control[]
        {
            new TextBlock { Text = message, MaxWidth = 380, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            buttons,
        });
        ok.Click += (_, _) => win.Close(true);
        cancel.Click += (_, _) => win.Close(false);
        Loc.Apply(win);
        return await win.ShowDialog<bool>(owner);
    }

    public static async Task Info(Window owner, string title, string message)
    {
        var buttons = Buttons(out var ok, out _, "OK");
        buttons.Children.RemoveAt(1); // «Отмена» в информационном окне не нужна
        var win = Make(title);
        ((StackPanel)win.Content!).Children.AddRange(new Control[]
        {
            new TextBlock { Text = message, MaxWidth = 460, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            buttons,
        });
        ok.Click += (_, _) => win.Close();
        Loc.Apply(win);
        await win.ShowDialog(owner);
    }

    /// <summary>Вопрос о ключе сервера. Для изменившегося ключа кнопкой по умолчанию остаётся «Отмена».</summary>
    public static async Task<bool> HostKey(Window owner, Storage.HostKeyRequest r)
    {
        var changed = r.Issue == Storage.HostKeyIssue.Changed;
        var win = Make(changed ? "Ключ сервера изменился!" : "Новый сервер");
        var accepted = false;

        var print = new TextBox
        {
            Text = r.Fingerprint, IsReadOnly = true, MinWidth = 440,
            FontFamily = new Avalonia.Media.FontFamily("Consolas, monospace"),
        };
        var ok = new Button { Content = changed ? "Принять новый ключ" : "Доверять и подключиться", IsDefault = !changed };
        var cancel = new Button { Content = "Отмена", IsCancel = true, IsDefault = changed };
        ok.Click += (_, _) => { accepted = true; win.Close(); };
        cancel.Click += (_, _) => win.Close();

        var rows = new List<Control>();
        if (changed)
        {
            rows.Add(new TextBlock
            {
                Text = Loc.F("ВНИМАНИЕ: ключ сервера {0}:{1} не совпадает с запомненным. ", r.Host, r.Port) +
                       Loc.T("Это может быть переустановка сервера, а может быть подмена (атака «человек посередине»). Если сервер вы не переустанавливали, не подключайтесь."),
                Foreground = Avalonia.Media.Brushes.IndianRed,
                MaxWidth = 440,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
            rows.Add(new TextBlock { Text = "Запомненный отпечаток:" });
            rows.Add(new TextBox { Text = r.KnownFingerprint, IsReadOnly = true, FontFamily = print.FontFamily });
            rows.Add(new TextBlock { Text = Loc.F("Полученный сейчас ({0}):", r.Algorithm) });
        }
        else
        {
            rows.Add(new TextBlock
            {
                Text = Loc.F("Сервер {0}:{1} раньше не встречался. Проверьте отпечаток ключа ({2}), ", r.Host, r.Port, r.Algorithm) +
                       Loc.T("если есть возможность сверить его с сервером."),
                MaxWidth = 440,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
        }
        rows.Add(print);
        rows.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { ok, cancel },
        });

        ((StackPanel)win.Content!).Children.AddRange(rows);
        Loc.Apply(win);
        await win.ShowDialog(owner);
        return accepted;
    }

    public enum ConflictChoice { Replace, Skip, ReplaceAll, SkipAll, Cancel }

    /// <summary>Вопрос о совпадении имени. Кнопки «для всех» показываются, если впереди есть ещё элементы.</summary>
    public static async Task<ConflictChoice> Conflict(Window owner, string name, bool isFolder, bool more)
    {
        var message = isFolder
            ? Loc.F("Папка «{0}» уже существует.\nФайлы с одинаковыми именами внутри будут заменены.", name)
            : Loc.F("Файл «{0}» уже существует. Заменить его?", name);

        var win = Make("Совпадение имён");
        var result = ConflictChoice.Cancel;

        Button Choice(string text, ConflictChoice value, bool isDefault = false)
        {
            var b = new Button { Content = text, IsDefault = isDefault, Margin = new Avalonia.Thickness(0, 0, 6, 6) };
            b.Click += (_, _) => { result = value; win.Close(); };
            return b;
        }

        var buttons = new WrapPanel { MaxWidth = 420 };
        buttons.Children.Add(Choice(isFolder ? "Объединить" : "Заменить", ConflictChoice.Replace, true));
        buttons.Children.Add(Choice("Пропустить", ConflictChoice.Skip));
        if (more)
        {
            buttons.Children.Add(Choice("Заменить все", ConflictChoice.ReplaceAll));
            buttons.Children.Add(Choice("Пропустить все", ConflictChoice.SkipAll));
        }
        buttons.Children.Add(Choice("Отмена", ConflictChoice.Cancel));

        ((StackPanel)win.Content!).Children.AddRange(new Control[]
        {
            new TextBlock { Text = message, MaxWidth = 420, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            buttons,
        });
        Loc.Apply(win);
        await win.ShowDialog(owner);
        return result;
    }

    /// <summary>
    /// Ввод мастер-пароля. validate получает пароль и возвращает текст ошибки или null, если пароль принят.
    /// </summary>
    public static async Task<bool> Unlock(Window owner, bool create, Func<string, Task<string?>> validate)
    {
        var pass = new TextBox { PasswordChar = '*', MinWidth = 300 };
        var confirm = new TextBox { PasswordChar = '*', IsVisible = create };
        var error = new TextBlock { Foreground = Avalonia.Media.Brushes.IndianRed, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 300 };
        var buttons = Buttons(out var ok, out var cancel, create ? "Создать" : "Открыть");
        var win = Make(create ? "Создание хранилища" : "Мастер-пароль");

        var rows = new List<Control>
        {
            new TextBlock
            {
                Text = create
                    ? "Придумайте мастер-пароль. Им шифруются все сессии и пароли.\nВосстановить его нельзя."
                    : "Введите мастер-пароль",
            },
            pass,
        };
        if (create) { rows.Add(new TextBlock { Text = "Повторите пароль" }); rows.Add(confirm); }
        rows.Add(error);
        rows.Add(buttons);
        ((StackPanel)win.Content!).Children.AddRange(rows);

        ok.Click += async (_, _) =>
        {
            var pw = pass.Text ?? "";
            if (create && pw.Length < 8) { error.Text = Loc.T("Минимум 8 символов"); return; }
            if (create && pw != confirm.Text) { error.Text = Loc.T("Пароли не совпадают"); return; }
            if (pw.Length == 0) { error.Text = Loc.T("Введите пароль"); return; }

            ok.IsEnabled = pass.IsEnabled = false;
            error.Text = Loc.T("Проверка…");
            var err = await validate(pw);
            if (err is null) { win.Close(true); return; }
            error.Text = err;
            ok.IsEnabled = pass.IsEnabled = true;
            pass.Focus();
        };
        cancel.Click += (_, _) => win.Close(false);
        win.Opened += (_, _) => pass.Focus();
        Loc.Apply(win);
        return await win.ShowDialog<bool>(owner);
    }

    /// <summary>Смена мастер-пароля. apply получает текущий и новый пароль и возвращает текст ошибки или null.</summary>
    public static async Task<bool> ChangePassword(Window owner, Func<string, string, Task<string?>> apply)
    {
        var current = new TextBox { PasswordChar = '*', MinWidth = 300 };
        var next = new TextBox { PasswordChar = '*' };
        var repeat = new TextBox { PasswordChar = '*' };
        var error = new TextBlock { Foreground = Avalonia.Media.Brushes.IndianRed, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 300 };
        var buttons = Buttons(out var ok, out var cancel, "Сменить");
        var win = Make("Смена мастер-пароля");

        ((StackPanel)win.Content!).Children.AddRange(new Control[]
        {
            new TextBlock { Text = "Текущий пароль" }, current,
            new TextBlock { Text = "Новый пароль" }, next,
            new TextBlock { Text = "Повторите новый пароль" }, repeat,
            error, buttons,
        });

        ok.Click += async (_, _) =>
        {
            var cur = current.Text ?? "";
            var pw = next.Text ?? "";
            if (cur.Length == 0) { error.Text = Loc.T("Введите пароль"); return; }
            if (pw.Length < 8) { error.Text = Loc.T("Минимум 8 символов"); return; }
            if (pw != repeat.Text) { error.Text = Loc.T("Пароли не совпадают"); return; }

            ok.IsEnabled = false;
            error.Text = Loc.T("Проверка…");
            var err = await apply(cur, pw);
            if (err is null) { win.Close(true); return; }
            error.Text = Loc.T(err);
            ok.IsEnabled = true;
        };
        cancel.Click += (_, _) => win.Close(false);
        win.Opened += (_, _) => current.Focus();
        Loc.Apply(win);
        return await win.ShowDialog<bool>(owner);
    }
}
