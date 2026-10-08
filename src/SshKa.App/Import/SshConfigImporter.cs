using SshKa.App.Models;

namespace SshKa.App.Import;

/// <summary>
/// Читает ~/.ssh/config: каждый Host без подстановочных знаков становится сессией.
/// Значения из блока "Host *" используются как умолчания. Include, Match и ProxyJump не поддерживаются.
/// </summary>
public static class SshConfigImporter
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");

    public static ImportResult ParseFile(string path) => Parse(File.ReadAllText(path));

    private sealed class Block
    {
        public List<string> Aliases { get; } = new();
        public string? HostName, User, Port, IdentityFile;
    }

    public static ImportResult Parse(string text)
    {
        var blocks = new List<Block>();
        Block? current = null;

        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            // "Ключ значение" или "Ключ=значение"
            var split = line.IndexOfAny(new[] { ' ', '\t', '=' });
            if (split <= 0) continue;
            var key = line[..split];
            var value = line[(split + 1)..].Trim().TrimStart('=').Trim().Trim('"');

            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                current = new Block();
                current.Aliases.AddRange(value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
                blocks.Add(current);
            }
            else if (key.Equals("Match", StringComparison.OrdinalIgnoreCase))
            {
                current = null; // условные блоки не поддерживаем, их параметры не должны попасть в предыдущий Host
            }
            else if (current is not null)
            {
                if (key.Equals("HostName", StringComparison.OrdinalIgnoreCase)) current.HostName ??= value;
                else if (key.Equals("User", StringComparison.OrdinalIgnoreCase)) current.User ??= value;
                else if (key.Equals("Port", StringComparison.OrdinalIgnoreCase)) current.Port ??= value;
                else if (key.Equals("IdentityFile", StringComparison.OrdinalIgnoreCase)) current.IdentityFile ??= value;
            }
        }

        var defaults = blocks.FirstOrDefault(b => b.Aliases.Contains("*"));
        var result = new ImportResult();

        foreach (var block in blocks)
        {
            foreach (var alias in block.Aliases)
            {
                if (alias.IndexOfAny(new[] { '*', '?', '!' }) >= 0) continue;

                var host = block.HostName ?? alias;
                var key = block.IdentityFile ?? defaults?.IdentityFile;
                var item = new SessionItem
                {
                    Name = alias,
                    Host = host,
                    Port = int.TryParse(block.Port ?? defaults?.Port, out var port) && port is > 0 and <= 65535 ? port : 22,
                    Username = block.User ?? defaults?.User ?? "",
                };
                if (!string.IsNullOrWhiteSpace(key))
                {
                    item.Auth = AuthKind.Key;
                    item.KeyPath = ExpandHome(key);
                }
                result.Items.Add(item);
                result.SessionCount++;
            }
        }
        return result;
    }

    private static string ExpandHome(string path)
    {
        if (path == "~" || path.StartsWith("~/") || path.StartsWith("~\\"))
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        return path.Replace('/', Path.DirectorySeparatorChar);
    }
}
