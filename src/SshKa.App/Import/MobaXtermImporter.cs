using SshKa.App.Localization;
using System.Text;
using System.Text.RegularExpressions;
using SshKa.App.Models;

namespace SshKa.App.Import;

/// <summary>
/// Читает сессии из .mxtsessions (экспорт MobaXterm) или из секций [Bookmarks*] файла MobaXterm.ini.
/// Формат: [Bookmarks_N] с SubRep=Папка\Подпапка и строками Имя=#иконка#тип%хост%порт%логин%...#...
/// Пароли в эти файлы не попадают, поэтому не импортируются.
/// </summary>
public static partial class MobaXtermImporter
{
    private static readonly Dictionary<int, string> TypeNames = new()
    {
        [2] = "Rsh", [3] = "XDMCP", [4] = "RDP", [5] = "VNC", [6] = "FTP", [7] = "SFTP",
        [8] = "Serial", [9] = "File", [10] = "Shell", [11] = "Browser", [12] = "Mosh", [13] = "AWS S3", [14] = "WSL",
    };

    [GeneratedRegex(@"\.(ppk|pem|key)$|(^|[\\/])id_(rsa|ed25519|ecdsa|dsa)$", RegexOptions.IgnoreCase)]
    private static partial Regex KeyFile();

    public static ImportResult ParseFile(string path) => Parse(ReadText(path));

    /// <summary>Файлы MobaXterm бывают в UTF-16 и в однобайтовой кодировке — смотрим на BOM.</summary>
    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return Encoding.UTF8.GetString(bytes);
    }

    public static ImportResult Parse(string text)
    {
        var result = new ImportResult();
        string[] folderPath = Array.Empty<string>();
        var inBookmarks = false;

        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('['))
            {
                // нас интересуют только закладки; остальные секции ini (настройки, цвета) пропускаем
                inBookmarks = line.StartsWith("[Bookmarks", StringComparison.OrdinalIgnoreCase);
                folderPath = Array.Empty<string>();
                continue;
            }
            if (!inBookmarks) continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq];
            var value = line[(eq + 1)..];

            if (key == "SubRep")
            {
                folderPath = value.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                // пустая папка тоже должна появиться
                ImportResult.FolderContents(result.Items, folderPath);
                continue;
            }
            if (key == "ImgNum") continue;

            var session = ParseSession(key, value, out var skipReason);
            if (session is null)
            {
                if (skipReason is not null) result.Skipped.Add($"{key} ({skipReason})");
                continue;
            }

            ImportResult.FolderContents(result.Items, folderPath).Add(session);
            result.SessionCount++;
        }
        return result;
    }

    private static SessionItem? ParseSession(string name, string value, out string? skipReason)
    {
        skipReason = null;

        // "#109#0%host%22%user%...#прочее" -> ["", "109", "0%host%22%user%...", ...]
        var parts = value.Split('#');
        if (parts.Length < 3 || !value.StartsWith('#')) return null;

        var fields = parts[2].Split('%');
        if (fields.Length < 3 || !int.TryParse(fields[0], out var type)) return null;

        if (type is not (0 or 1))
        {
            skipReason = TypeNames.TryGetValue(type, out var tn) ? tn : Loc.F("тип {0}", type);
            return null;
        }

        string Field(int i) => i < fields.Length ? fields[i].Trim() : "";

        var item = new SessionItem
        {
            Name = name,
            Protocol = type == 0 ? Protocol.Ssh : Protocol.Telnet,
            Host = Field(1),
            Port = int.TryParse(Field(2), out var port) && port is > 0 and <= 65535 ? port : (type == 0 ? 22 : 23),
            Username = Field(3) is "<default>" ? "" : Field(3),
        };

        if (item.Host.Length == 0)
        {
            skipReason = Loc.T("нет хоста");
            return null;
        }

        // путь к приватному ключу лежит в одном из последующих полей; позиция у разных версий разная
        if (type == 0)
        {
            var key = fields.Skip(4).FirstOrDefault(f => KeyFile().IsMatch(f.Trim()));
            if (key is not null)
            {
                item.Auth = AuthKind.Key;
                item.KeyPath = key.Trim().Replace("_ProfileDir_",
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            }
        }
        return item;
    }
}
