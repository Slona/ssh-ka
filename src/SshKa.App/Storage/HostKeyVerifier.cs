using System.Text.Json;
using Renci.SshNet;

namespace SshKa.App.Storage;

public enum HostKeyIssue
{
    /// <summary>Хост (или этот тип ключа) раньше не встречался.</summary>
    New,

    /// <summary>Ключ того же типа у этого хоста отличается от запомненного — возможна подмена сервера.</summary>
    Changed,
}

public sealed record HostKeyRequest(string Host, int Port, string Algorithm, string Fingerprint, HostKeyIssue Issue, string? KnownFingerprint);

/// <summary>
/// Проверка ключей серверов по принципу known_hosts: первый раз спрашиваем и запоминаем,
/// при несовпадении предупреждаем. Подключаются к нему все SSH-клиенты (шелл, SFTP, SCP).
/// </summary>
public static class HostKeyVerifier
{
    private static string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SshKa", "known_hosts.json");

    private static readonly object Gate = new();
    private static Dictionary<string, Dictionary<string, string>>? _known; // "host:port" -> алгоритм -> отпечаток

    /// <summary>
    /// Вопрос пользователю; вызывается из фонового потока подключения, должен вернуть true, чтобы доверять ключу.
    /// Пока обработчик не задан, неизвестные и изменившиеся ключи отклоняются.
    /// </summary>
    public static Func<HostKeyRequest, bool>? Confirm { get; set; }

    /// <summary>Подменяет файл хранилища (для тестов) и сбрасывает кэш.</summary>
    internal static void UseStore(string path)
    {
        lock (Gate)
        {
            FilePath = path;
            _known = null;
        }
    }

    public static void Attach(BaseClient client)
    {
        var info = client.ConnectionInfo;
        client.HostKeyReceived += (_, e) =>
            e.CanTrust = Verify(info.Host, info.Port, e.HostKeyName, "SHA256:" + e.FingerPrintSHA256.TrimStart().Replace("SHA256:", ""));
    }

    private static bool Verify(string host, int port, string algorithm, string fingerprint)
    {
        var id = $"{host.ToLowerInvariant()}:{port}";
        HostKeyRequest? request = null;

        lock (Gate)
        {
            var known = Load();
            if (known.TryGetValue(id, out var keys))
            {
                if (keys.TryGetValue(algorithm, out var stored))
                {
                    if (stored == fingerprint) return true;
                    request = new HostKeyRequest(host, port, algorithm, fingerprint, HostKeyIssue.Changed, stored);
                }
                else
                {
                    // хост знаком, но этим типом ключа ещё не подключались
                    request = new HostKeyRequest(host, port, algorithm, fingerprint, HostKeyIssue.New, null);
                }
            }
            else
            {
                request = new HostKeyRequest(host, port, algorithm, fingerprint, HostKeyIssue.New, null);
            }
        }

        // диалог показываем вне блокировки: он может висеть сколько угодно
        if (Confirm?.Invoke(request) != true) return false;

        lock (Gate)
        {
            var known = Load();
            if (!known.TryGetValue(id, out var keys)) known[id] = keys = new();
            keys[algorithm] = fingerprint;
            Save(known);
        }
        return true;
    }

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        if (_known is not null) return _known;
        try
        {
            if (File.Exists(FilePath))
                _known = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(FilePath));
        }
        catch
        {
            // повреждённый файл: начинаем с пустого, пользователь подтвердит ключи заново
        }
        return _known ??= new();
    }

    private static void Save(Dictionary<string, Dictionary<string, string>> known)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(known, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
