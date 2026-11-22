using SshKa.App.Localization;

namespace SshKa.App.Files;

/// <summary>Файловые операции с повторами: антивирус и облачные синхронизации держат свежий файл
/// доли секунды, и «файл занят» почти всегда проходит при следующей попытке.</summary>
public static class FileIo
{
    /// <summary>Паузы между попытками; последняя попытка выполняется без последующей паузы.</summary>
    private static readonly int[] RetryWaitsMs = [0, 300, 1000];

    /// <summary>Файл держит другой процесс (sharing violation) или доступ запрещён.</summary>
    public static bool IsLocked(Exception ex) =>
        ex is IOException { HResult: 32 or 33 } || ex is UnauthorizedAccessException;

    /// <summary>Выполняет операцию, повторяя при блокировке файла; если блокировка пережила все
    /// попытки, исключение выходит наружу (вызывающий код переводит его в понятное сообщение).</summary>
    public static void Retry(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { action(); return; }
            catch (Exception ex) when (IsLocked(ex) && attempt < RetryWaitsMs.Length - 1)
            {
                Thread.Sleep(RetryWaitsMs[attempt + 1]);
            }
        }
    }

    public static FileStream Create(string path)
    {
        FileStream? stream = null;
        Retry(() => stream = File.Create(path));
        return stream!;
    }

    public static FileStream OpenRead(string path)
    {
        FileStream? stream = null;
        Retry(() => stream = File.OpenRead(path));
        return stream!;
    }

    public static void MoveOverwriting(string source, string target) =>
        Retry(() => File.Move(source, target, overwrite: true));

    /// <summary>Понятное сообщение вместо «The process cannot access the file…».</summary>
    public static IOException LockedError(string name, Exception inner) =>
        new(Loc.F("Файл «{0}» занят другим приложением. Закройте его и повторите передачу.", name), inner);
}
