using SshKa.App.Localization;

namespace SshKa.App.Files;

/// <summary>Файловые операции с повторами: антивирус и облачные синхронизации держат свежий файл
/// доли секунды, и «файл занят» почти всегда проходит при следующей попытке.</summary>
public static class FileIo
{
    /// <summary>Паузы между попытками; последняя попытка выполняется без последующей паузы.</summary>
    private static readonly int[] RetryWaitsMs = [0, 300, 1000, 2000];

    /// <summary>Файл держит другой процесс (sharing/lock violation) или доступ запрещён.
    /// В младшем слове HRESULT лежит код Win32: 32 — sharing violation, 33 — lock violation;
    /// .NET записывает туда полный HRESULT (0x80070020/21), поэтому сравниваем младшее слово.</summary>
    public static bool IsLocked(Exception ex)
    {
        if (ex is UnauthorizedAccessException) return true;
        return ex is IOException && (ex.HResult & 0xFFFF) is 32 or 33;
    }

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

    /// <summary>Соседнее свободное имя «файл (1).ext», «файл (2).ext»… — для случая,
    /// когда цель держит другая программа, а скачивать всё же нужно.</summary>
    public static string Alternate(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }
}
