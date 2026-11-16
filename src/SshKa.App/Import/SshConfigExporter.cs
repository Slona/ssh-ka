using System.Text;
using SshKa.App.Localization;
using SshKa.App.Models;

namespace SshKa.App.Import;

/// <summary>
/// Пишет SSH-сессии в формат ~/.ssh/config: Host-блоки с HostName, User, Port и IdentityFile.
/// Telnet в этот формат не помещается — такие сессии пропускаются. Папок в ssh config не существует:
/// все сессии идут одним плоским списком. Пароли ssh config не хранит — они не экспортируются.
/// </summary>
public static class SshConfigExporter
{
    public static ExportResult Write(IList<SessionItem> root)
    {
        var result = new ExportResult();
        var text = new StringBuilder();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Walk(IList<SessionItem> level)
        {
            foreach (var item in level)
            {
                if (item.IsFolder) { Walk(item.Children); continue; }
                if (item.Protocol != Protocol.Ssh)
                {
                    result.Skipped.Add($"{item.Name} (telnet)");
                    continue;
                }
                if (item.Host.Length == 0)
                {
                    result.Skipped.Add($"{item.Name} ({Loc.T("нет хоста")})");
                    continue;
                }

                text.AppendLine($"Host {Alias(item.Name, item.Host, used)}");
                text.AppendLine($"    HostName {item.Host}");
                if (item.Username.Length > 0) text.AppendLine($"    User {item.Username}");
                if (item.Port != 22) text.AppendLine($"    Port {item.Port}");
                if (item.Auth == AuthKind.Key && item.KeyPath.Length > 0)
                    text.AppendLine($"    IdentityFile {Quoted(Tilde(item.KeyPath))}");
                text.AppendLine();
                result.Count++;
            }
        }

        Walk(root);
        result.Text = text.ToString();
        return result;
    }

    /// <summary>Имя сессии → алиас Host: без пробелов и подстановочных знаков, уникальный.</summary>
    private static string Alias(string name, string host, HashSet<string> used)
    {
        var clean = name.Trim();
        foreach (var c in new[] { ' ', '\t', '*', '?', '!', '=', '#', ':', '/', '\\' })
            clean = clean.Replace(c, '_');
        if (clean.Length == 0) clean = host;
        var alias = clean;
        for (var n = 2; !used.Add(alias); n++) alias = $"{clean}-{n}";
        return alias;
    }

    /// <summary>Путь под профилем пользователя пишется через ~/… — файл останется верным на другой машине.</summary>
    private static string Tilde(string path)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return path; }
        if (full.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
            return "~/" + full[(profile.Length + 1)..].Replace('\\', '/');
        return full.Replace('\\', '/');
    }

    private static string Quoted(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
}
