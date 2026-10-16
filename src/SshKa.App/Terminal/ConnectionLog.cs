namespace SshKa.App.Terminal;

/// <summary>
/// Короткий журнал событий SSH-соединений (обрывы, ответы на проверки). Нужен для разбора случаев, когда сессия «зависла»:
/// лежит в %APPDATA%\SshKa\connection.log, при превышении 200 КБ начинается заново. Секретов в нём нет.
/// </summary>
internal static class ConnectionLog
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SshKa", "connection.log");

    private static readonly object Gate = new();

    public static void Write(string host, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 200_000) File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{host}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // журнал не должен ничего ломать
        }
    }
}
