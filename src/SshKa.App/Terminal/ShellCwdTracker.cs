using System.Text.RegularExpressions;
using Renci.SshNet;

namespace SshKa.App.Terminal;

/// <summary>Редактор, работающий сейчас на переднем плане терминала: процесс и открытый в нём файл (если удалось определить).</summary>
public sealed record ForegroundEditor(int Pid, string? File);

/// <summary>
/// Следит за терминалом сессии, ничего не вводя в него: находит pts нашего шелла (по разнице /dev/pts до и после
/// открытия шелла) и раз в секунду, а также по сигналу Poke, узнаёт на сервере текущий каталог шелла
/// (/proc/PID/cwd лидера сессии) и редактор на переднем плане с именем файла (по ps).
/// Работает на Linux; на остальных системах просто молчит.
/// </summary>
public sealed partial class ShellCwdTracker : IDisposable
{
    private const int MaxFindAttempts = 15;
    private const string Marker = "@@@";

    private static readonly HashSet<string> Editors = new(StringComparer.Ordinal)
    {
        "vim", "vi", "view", "vimdiff", "nvim", "rvim", "rview", "nano", "rnano", "pico", "micro", "mcedit",
        "emacs", "joe", "ne", "hx", "helix", "kak",
    };

    private readonly SshShellConnection _conn;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private string? _pts;
    private string? _last;

    /// <summary>Шелл перешёл в другой каталог.</summary>
    public event Action<string>? CwdChanged;

    /// <summary>После каждого опроса: какой редактор сейчас на переднем плане (null — никакого).</summary>
    public event Action<ForegroundEditor?>? EditorPolled;

    public ShellCwdTracker(SshShellConnection conn) => _conn = conn;

    public void Start() => _ = Task.Run(LoopAsync);

    public void Dispose() => _cts.Cancel();

    /// <summary>Просит опросить сервер немедленно, не дожидаясь очередной секунды.</summary>
    public void Poke()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* опрос уже запрошен */ }
    }

    /// <summary>Просит процесс перерисоваться (SIGWINCH), чтобы редактор заново вывел экран уже с подсветкой.</summary>
    public Task RequestRedrawAsync(int pid) => Task.Run(() =>
    {
        try
        {
            using var cmd = _conn.Client.CreateCommand($"kill -WINCH {pid} 2>/dev/null");
            cmd.Execute();
        }
        catch { /* процесс уже завершился или нет прав — тогда подсветка появится при следующей перерисовке */ }
    });

    /// <summary>Номера терминалов /dev/pts, существующие на сервере сейчас.</summary>
    public static HashSet<string> ListPts(SshClient client)
    {
        try
        {
            using var cmd = client.CreateCommand("ls /dev/pts 2>/dev/null");
            return cmd.Execute().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => PtsNumber().IsMatch(s)).ToHashSet();
        }
        catch
        {
            return new HashSet<string>();
        }
    }

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex PtsNumber();

    [GeneratedRegex(@"^\s*(\d+)\s+(\S+)\s+(.*)$")]
    private static partial Regex PsLine();

    private async Task LoopAsync()
    {
        var ct = _cts.Token;
        var attempts = 0;
        while (!ct.IsCancellationRequested && _conn.Client.IsConnected)
        {
            try
            {
                if (_pts is null)
                {
                    _pts = FindPts();
                    if (_pts is null && ++attempts >= MaxFindAttempts) return; // не Linux или нет /dev/pts
                }

                if (_pts is not null) Poll(_pts);
            }
            catch
            {
                // команда не выполнилась (соединение закрывается и т. п.) — пробуем в следующий раз
            }

            try { await _wake.WaitAsync(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private string? FindPts()
    {
        var created = ListPts(_conn.Client).Except(_conn.PtsBefore).Select(int.Parse).ToList();
        return created.Count == 0 ? null : created.Max().ToString();
    }

    private void Poll(string pts)
    {
        // лидер сессии (pid == sid) на нашем терминале и есть шелл; затем список процессов терминала
        var script =
            $"pid=$(ps -t pts/{pts} -o pid=,sid= 2>/dev/null | awk '$1==$2{{print $1; exit}}'); " +
            "[ -n \"$pid\" ] && readlink /proc/$pid/cwd; " +
            $"echo {Marker}; ps -t pts/{pts} -o pid=,stat=,args= 2>/dev/null";
        using var cmd = _conn.Client.CreateCommand(script);
        var output = cmd.Execute().Replace("\r", "");

        var split = output.IndexOf(Marker + "\n", StringComparison.Ordinal);
        if (split < 0) return;

        var cwd = output[..split].Trim();
        if (!string.IsNullOrEmpty(cwd) && cwd != _last)
        {
            _last = cwd;
            CwdChanged?.Invoke(cwd);
        }

        EditorPolled?.Invoke(ParseForeground(output[(split + Marker.Length + 1)..]));
    }

    /// <summary>Находит среди процессов терминала (вывод ps -o pid=,stat=,args=) редактор на переднем плане.</summary>
    internal static ForegroundEditor? ParseForeground(string psOutput)
    {
        foreach (var line in psOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = PsLine().Match(line);
            if (!m.Success || !m.Groups[2].Value.Contains('+')) continue; // '+' — группа процессов на переднем плане

            var tokens = m.Groups[3].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;

            var program = tokens[0].Replace('\\', '/');
            program = program[(program.LastIndexOf('/') + 1)..].TrimStart('-');
            if (!Editors.Contains(program)) continue;

            // файл — позиционный аргумент; опции и +N/+команда пропускаем. Из нескольких берём первый с известным расширением.
            var candidates = tokens.Skip(1).Where(t => !t.StartsWith('-') && !t.StartsWith('+')).ToList();
            var file = candidates.FirstOrDefault(Highlight.CodeLanguages.IsKnown) ?? candidates.FirstOrDefault();
            return new ForegroundEditor(int.Parse(m.Groups[1].Value), file);
        }
        return null;
    }
}
