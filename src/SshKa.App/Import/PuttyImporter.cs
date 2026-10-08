using SshKa.App.Localization;
using Microsoft.Win32;
using SshKa.App.Models;

namespace SshKa.App.Import;

/// <summary>Читает сохранённые сессии PuTTY из реестра Windows (HKCU\Software\SimonTatham\PuTTY\Sessions).</summary>
public static class PuttyImporter
{
    private const string SessionsKey = @"Software\SimonTatham\PuTTY\Sessions";

    public static bool IsAvailable => OperatingSystem.IsWindows();

    public static ImportResult ReadRegistry()
    {
        var result = new ImportResult();
        if (!OperatingSystem.IsWindows()) return result;

        using var root = Registry.CurrentUser.OpenSubKey(SessionsKey);
        if (root is null) return result;

        foreach (var encoded in root.GetSubKeyNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            // имена сессий хранятся в URL-кодировании: "My%20Server"
            var name = Uri.UnescapeDataString(encoded);
            using var key = root.OpenSubKey(encoded);
            if (key is null) continue;

            var item = Build(name, key.GetValue("Protocol") as string, key.GetValue("HostName") as string,
                key.GetValue("PortNumber"), key.GetValue("UserName") as string, key.GetValue("PublicKeyFile") as string,
                out var skip);
            if (item is null)
            {
                if (skip is not null) result.Skipped.Add($"{name} ({skip})");
                continue;
            }
            result.Items.Add(item);
            result.SessionCount++;
        }
        return result;
    }

    /// <summary>Превращает значения реестра в сессию; вынесено отдельно, чтобы проверять без реестра.</summary>
    internal static SessionItem? Build(string name, string? protocol, string? host, object? port,
        string? user, string? keyFile, out string? skipReason)
    {
        skipReason = null;
        protocol = (protocol ?? "ssh").ToLowerInvariant();
        if (protocol is not ("ssh" or "telnet"))
        {
            skipReason = protocol;
            return null;
        }

        host = host?.Trim() ?? "";
        if (host.Length == 0) // в PuTTY бывают шаблоны без хоста, например "Default Settings"
        {
            skipReason = Loc.T("нет хоста");
            return null;
        }

        // PuTTY допускает запись user@host прямо в поле хоста
        var username = user?.Trim() ?? "";
        var at = host.IndexOf('@');
        if (at > 0)
        {
            if (username.Length == 0) username = host[..at];
            host = host[(at + 1)..];
        }

        var isSsh = protocol == "ssh";
        var item = new SessionItem
        {
            Name = name,
            Protocol = isSsh ? Protocol.Ssh : Protocol.Telnet,
            Host = host,
            Port = port is int p and > 0 and <= 65535 ? p : (isSsh ? 22 : 23),
            Username = username,
        };

        if (isSsh && !string.IsNullOrWhiteSpace(keyFile))
        {
            item.Auth = AuthKind.Key;
            item.KeyPath = keyFile.Trim();
        }
        return item;
    }
}
