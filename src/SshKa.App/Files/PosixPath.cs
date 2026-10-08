namespace SshKa.App.Files;

/// <summary>Операции над путями удалённого Unix-сервера (всегда '/', независимо от ОС клиента).</summary>
public static class PosixPath
{
    public static string Combine(string dir, string name) =>
        dir.EndsWith('/') ? dir + name : dir + "/" + name;

    /// <summary>Приводит абсолютный путь к каноническому виду: убирает ".", ".." и повторные слэши.</summary>
    public static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var p in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (p == ".") continue;
            if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(p);
        }
        return "/" + string.Join('/', parts);
    }

    public static string Parent(string path)
    {
        var n = Normalize(path);
        var i = n.LastIndexOf('/');
        return i <= 0 ? "/" : n[..i];
    }

    /// <summary>Экранирует строку для подстановки в команду оболочки.</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
