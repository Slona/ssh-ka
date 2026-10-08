using SshKa.App.Localization;
using System.Text;
using Renci.SshNet;
using SshKa.App.Files;

namespace SshKa.App.Terminal;

/// <summary>Что нашлось на сервере и что нужно сделать, чтобы vim и nano подсвечивали код сами.</summary>
public sealed record EditorSetupPlan(
    string Home,
    bool VimInstalled, bool VimHasSyntax, bool VimAlready,
    bool NanoInstalled, string? NanoRcDir, bool NanoAlready)
{
    public const string VimComment = "\" Added by SshKa: syntax highlighting";
    public static readonly string[] VimLines = { "syntax on", "set background=dark" };
    public const string NanoComment = "# Added by SshKa: syntax highlighting";

    public bool NeedsVim => VimInstalled && VimHasSyntax && !VimAlready;
    public bool NeedsNano => NanoInstalled && NanoRcDir is not null && !NanoAlready;

    public string NanoIncludeLine => $"include \"{NanoRcDir}/*.nanorc\"";

    /// <summary>Описание для окна подтверждения: что уже работает, что недоступно и что именно будет записано.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();

        if (!VimInstalled) sb.AppendLine(Loc.T("• vim: не установлен."));
        else if (!VimHasSyntax) sb.AppendLine(Loc.T("• vim: собран без поддержки синтаксиса (нужен полноценный пакет vim, а не vim-tiny)."));
        else if (VimAlready) sb.AppendLine(Loc.T("• vim: подсветка уже включена в его настройках."));
        else sb.AppendLine(Loc.T("• vim: подсветка выключена. В ~/.vimrc будут добавлены строки:") + "\n    " + string.Join("\n    ", VimLines));

        if (!NanoInstalled) sb.AppendLine(Loc.T("• nano: не установлен."));
        else if (NanoRcDir is null) sb.AppendLine(Loc.T("• nano: на сервере нет файлов с правилами подсветки (*.nanorc)."));
        else if (NanoAlready) sb.AppendLine(Loc.T("• nano: подсветка уже включена в его настройках."));
        else sb.AppendLine(Loc.T("• nano: подсветка выключена. В ~/.nanorc будет добавлена строка:") + "\n    " + NanoIncludeLine);

        if (NeedsVim || NeedsNano)
        {
            sb.AppendLine();
            sb.AppendLine(Loc.T("Строки только дописываются в конец файла (если файла нет, он создаётся). " +
                          "Если файл уже был, рядом остаётся его копия с суффиксом .sshka-bak. " +
                          "Отменить можно, удалив добавленные строки с пометкой «Added by SshKa»."));
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>Серверная подсветка: настраивает сами редакторы, поэтому она точнее клиентской (понимает язык и многострочные конструкции).</summary>
public static class EditorSetup
{
    /// <param name="home">Каталог пользователя в виде, пригодном для оболочки; "$HOME" в работе, другой — для проверок.</param>
    public static Task<EditorSetupPlan> InspectAsync(SshClient client, string home = "$HOME") => Task.Run(() =>
    {
        var script = $$"""
            H="{{home}}"
            v=0; command -v vim >/dev/null 2>&1 && v=1
            vs=0; [ $v = 1 ] && vim --version 2>/dev/null | grep -q '+syntax' && vs=1
            va=0; for f in "$H/.vimrc" "$H/.vim/vimrc" /etc/vim/vimrc /etc/vimrc; do grep -Eqs '^[[:space:]]*syntax[[:space:]]+(on|enable)' "$f" && va=1; done
            n=0; command -v nano >/dev/null 2>&1 && n=1
            nd=""; for d in /usr/share/nano /usr/local/share/nano /opt/homebrew/share/nano; do if ls "$d"/*.nanorc >/dev/null 2>&1; then nd=$d; break; fi; done
            na=0; for f in "$H/.nanorc" "$H/.config/nano/nanorc" /etc/nanorc /usr/local/etc/nanorc; do grep -Eqs '^[[:space:]]*include' "$f" && na=1; done
            echo "home=$H"; echo "vim=$v"; echo "vimsyntax=$vs"; echo "vimalready=$va"
            echo "nano=$n"; echo "nanodir=$nd"; echo "nanoalready=$na"
            """;
        using var cmd = client.CreateCommand(script);
        var values = cmd.Execute().Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);

        bool Flag(string key) => values.GetValueOrDefault(key) == "1";
        var nanoDir = values.GetValueOrDefault("nanodir");
        return new EditorSetupPlan(
            values.GetValueOrDefault("home") ?? home,
            Flag("vim"), Flag("vimsyntax"), Flag("vimalready"),
            Flag("nano"), string.IsNullOrEmpty(nanoDir) ? null : nanoDir, Flag("nanoalready"));
    });

    /// <summary>Скрипт оболочки, который дописывает строки в файлы настроек (вынесен для проверки текста).</summary>
    internal static string BuildApplyScript(EditorSetupPlan plan, string home)
    {
        var lines = new List<string> { $"H=\"{home}\"", "set -e" };

        if (plan.NeedsVim)
        {
            lines.Add("if [ -f \"$H/.vimrc\" ]; then cp -p \"$H/.vimrc\" \"$H/.vimrc.sshka-bak\"; fi");
            lines.Add("printf '%s\\n' '' " + PosixPath.Quote(EditorSetupPlan.VimComment) + " "
                      + string.Join(" ", EditorSetupPlan.VimLines.Select(PosixPath.Quote)) + " >> \"$H/.vimrc\"");
        }
        if (plan.NeedsNano)
        {
            lines.Add("if [ -f \"$H/.nanorc\" ]; then cp -p \"$H/.nanorc\" \"$H/.nanorc.sshka-bak\"; fi");
            lines.Add("printf '%s\\n' '' " + PosixPath.Quote(EditorSetupPlan.NanoComment) + " "
                      + PosixPath.Quote(plan.NanoIncludeLine) + " >> \"$H/.nanorc\"");
        }
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>Дописывает нужные строки в ~/.vimrc и ~/.nanorc. Делается только то, что в плане отмечено как необходимое.</summary>
    public static Task ApplyAsync(SshClient client, EditorSetupPlan plan, string home = "$HOME") => Task.Run(() =>
    {
        using var cmd = client.CreateCommand(BuildApplyScript(plan, home));
        cmd.Execute();
        if (cmd.ExitStatus != 0)
            throw new IOException(string.IsNullOrWhiteSpace(cmd.Error) ? Loc.F("Команда завершилась с кодом {0}", cmd.ExitStatus) : cmd.Error.Trim());
    });
}
