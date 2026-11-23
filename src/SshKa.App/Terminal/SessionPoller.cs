using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Renci.SshNet;
using SshKa.App.Highlight;

namespace SshKa.App.Terminal;

public sealed record DiskUsage(string Mount, int UsedPercent);

/// <summary>Подробности по памяти для всплывающей подсказки (в килобайтах, как в /proc/meminfo).</summary>
public sealed record MemoryDetails(long TotalKb, long UsedKb, long AvailableKb, long CachedKb, long BuffersKb);

/// <summary>Счётчики одного сетевого интерфейса с момента загрузки сервера.</summary>
public sealed record NetInterface(string Name, long RxBytes, long TxBytes, long RxErrors, long RxDropped, long TxErrors, long TxDropped);

/// <summary>Показатели сервера для панели состояния. Скорости и загрузка CPU пусты в первом замере.</summary>
public sealed record ServerStats(
    string Hostname,
    double? CpuPercent,
    long MemUsedBytes,
    long MemTotalBytes,
    double? RxBytesPerSec,
    double? TxBytesPerSec,
    TimeSpan Uptime,
    IReadOnlyList<DiskUsage> Disks,
    string? Kernel = null,
    MemoryDetails? Memory = null,
    IReadOnlyList<NetInterface>? Interfaces = null,
    string? DfText = null);

/// <summary>Редактор, работающий сейчас на переднем плане терминала: процесс и открытый в нём файл (если удалось определить).</summary>
public sealed record ForegroundEditor(int Pid, string? File);

/// <summary>
/// Единственный опросщик сервера для открытой SSH-сессии. Раз в две секунды выполняет один скрипт
/// по одному exec-каналу: текущий каталог шелла, редактор на переднем плане и — если включена панель
/// состояния — показатели /proc и df. Раньше этими данными занимались три отдельных цикла, открывавшие
/// до трёх каналов в секунду. Во время файловых передач опрос не останавливается, а просто реже:
/// панель состояния должна показывать реальную загрузку сети, когда она максимальная.
/// </summary>
public sealed partial class SessionPoller : IDisposable
{
    private const int MaxFindAttempts = 15;
    private const int MaxStatsFailures = 5;
    private const string Marker = "@@@";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TransferInterval = TimeSpan.FromSeconds(6);

    private static readonly HashSet<string> Editors = new(StringComparer.Ordinal)
    {
        "vim", "vi", "view", "vimdiff", "nvim", "rvim", "rview", "nano", "rnano", "pico", "micro", "mcedit",
        "emacs", "joe", "ne", "hx", "helix", "kak",
    };

    // Один вызов — все данные. Разделитель между секциями — строка @@@.
    private const string StatsScript =
        "hostname; echo @@@; head -n1 /proc/stat; echo @@@; " +
        "grep -E '^(MemTotal|MemAvailable|Cached|Buffers):' /proc/meminfo; echo @@@; " +
        "cat /proc/net/dev; echo @@@; cut -d' ' -f1 /proc/uptime; echo @@@; " +
        "df -PkT 2>/dev/null; echo @@@; cat /proc/version 2>/dev/null; echo @@@; df -h 2>/dev/null";

    private readonly SshShellConnection _conn;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private string? _pts;
    private string? _lastCwd;
    private int _findAttempts;
    private volatile bool _includeStats;
    private volatile bool _transferActive;

    private Snapshot? _prev;
    private long _prevTicks;

    /// <summary>Шелл перешёл в другой каталог.</summary>
    public event Action<string>? CwdChanged;

    /// <summary>После каждого опроса: какой редактор сейчас на переднем плане (null — никакого).</summary>
    public event Action<ForegroundEditor?>? EditorPolled;

    /// <summary>Очередной замер показателей сервера (для панели состояния).</summary>
    public event Action<ServerStats>? StatsUpdated;

    /// <summary>Показатели получить не удаётся (не Linux, нет /proc) — панель состояния показывать не надо.</summary>
    public event Action? StatsUnsupported;

    public SessionPoller(SshShellConnection conn) => _conn = conn;

    public void Start() => _ = Task.Run(LoopAsync);

    public void Dispose() => _cts.Cancel();

    /// <summary>Просит опросить сервер немедленно, не дожидаясь очередного интервала.</summary>
    public void Poke()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* опрос уже запрошен */ }
    }

    /// <summary>Показатели сервера нужны (панель состояния включена) или нет.</summary>
    public void SetStatsEnabled(bool on) => _includeStats = on;

    /// <summary>Идёт файловая передача: опрос реже (TransferInterval), чтобы не нагружать линию,
    /// но панель состояния продолжает показывать реальные цифры.</summary>
    public void SetTransfersActive(bool on) => _transferActive = on;

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
        var statsFailures = 0;
        try
        {
            while (!ct.IsCancellationRequested && _conn.Client.IsConnected)
            {
                try
                {
                    if (_pts is null)
                    {
                        _pts = FindPts();
                        if (_pts is null && ++_findAttempts >= MaxFindAttempts)
                            return; // не Linux или нет /dev/pts
                    }

                    if (_pts is not null) Poll(_pts, ref statsFailures);
                    else if (_includeStats) PollStatsOnly(ref statsFailures);
                }
                catch
                {
                    // команда не выполнилась (соединение закрывается и т. п.) — пробуем в следующий раз
                }
                await _wake.WaitAsync(_transferActive ? TransferInterval : Interval, ct); // просыпаемся и по Poke
            }
        }
        catch (OperationCanceledException)
        {
            // соединение закрыто нами
        }
    }

    private string? FindPts()
    {
        var created = ListPts(_conn.Client).Except(_conn.PtsBefore).Select(int.Parse).ToList();
        return created.Count == 0 ? null : created.Max().ToString();
    }

    private void Poll(string pts, ref int statsFailures)
    {
        // лидер сессии (pid == sid) на нашем терминале и есть шелл; затем список процессов терминала,
        // а при включённой панели состояния — ещё и показатели сервера: всё одним exec-каналом
        var script =
            $"pid=$(ps -t pts/{pts} -o pid=,sid= 2>/dev/null | awk '$1==$2{{print $1; exit}}'); " +
            "[ -n \"$pid\" ] && readlink /proc/$pid/cwd; " +
            $"echo {Marker}; ps -t pts/{pts} -o pid=,stat=,args= 2>/dev/null";
        if (_includeStats) script += $"; echo {Marker}; " + StatsScript;

        using var cmd = _conn.Client.CreateCommand(script);
        var output = cmd.Execute().Replace("\r", "");

        var sections = output.Split(Marker + "\n");
        if (sections.Length < 2) return;

        var cwd = sections[0].Trim();
        if (!string.IsNullOrEmpty(cwd) && cwd != _lastCwd)
        {
            _lastCwd = cwd;
            CwdChanged?.Invoke(cwd);
        }
        EditorPolled?.Invoke(ParseForeground(sections[1]));

        if (_includeStats && sections.Length > 2)
            ReportStats(string.Join(Marker + "\n", sections.Skip(2)), ref statsFailures);
    }

    private void PollStatsOnly(ref int statsFailures)
    {
        using var cmd = _conn.Client.CreateCommand(StatsScript);
        ReportStats(cmd.Execute().Replace("\r", ""), ref statsFailures);
    }

    private void ReportStats(string output, ref int statsFailures)
    {
        var snap = ParseStats(output);
        if (snap is null)
        {
            // данных нет: не Linux, нет /proc или неожиданный вывод — после нескольких неудач сдаёмся
            if (++statsFailures >= MaxStatsFailures)
            {
                _includeStats = false;
                StatsUnsupported?.Invoke();
            }
            return;
        }

        statsFailures = 0;
        StatsUpdated?.Invoke(Compute(snap));
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

    private ServerStats Compute(Snapshot s)
    {
        var now = Stopwatch.GetTimestamp();
        double? cpu = null, rx = null, tx = null;

        if (_prev is { } p)
        {
            var seconds = (now - _prevTicks) / (double)Stopwatch.Frequency;
            var dTotal = s.CpuTotal - p.CpuTotal;
            var dIdle = s.CpuIdle - p.CpuIdle;
            if (dTotal > 0) cpu = Math.Clamp(100.0 * (1.0 - (double)dIdle / dTotal), 0, 100);

            if (seconds > 0 && s.Rx >= p.Rx && s.Tx >= p.Tx) // счётчики могли обнулиться — тогда пропускаем замер
            {
                rx = (s.Rx - p.Rx) / seconds;
                tx = (s.Tx - p.Tx) / seconds;
            }
        }

        _prev = s;
        _prevTicks = now;

        return new ServerStats(
            s.Host, cpu,
            (s.MemTotalKb - s.MemAvailKb) * 1024, s.MemTotalKb * 1024,
            rx, tx, TimeSpan.FromSeconds(s.UptimeSeconds), s.Disks,
            s.Kernel, s.Memory, s.Interfaces, s.DfText);
    }

    internal sealed record Snapshot(
        string Host, long CpuTotal, long CpuIdle, long MemTotalKb, long MemAvailKb,
        long Rx, long Tx, double UptimeSeconds, IReadOnlyList<DiskUsage> Disks,
        string? Kernel = null, MemoryDetails? Memory = null, IReadOnlyList<NetInterface>? Interfaces = null, string? DfText = null);

    // псевдофайловые системы и служебные точки монтирования, которые в панели только мешают
    private static readonly HashSet<string> SkipTypes = new() { "devtmpfs", "squashfs", "overlay", "efivarfs", "iso9660", "proc", "sysfs", "cgroup2" };
    private static readonly string[] SkipMountPrefixes = { "/run", "/sys", "/proc", "/dev", "/snap", "/var/lib/docker", "/var/snap" };

    internal static Snapshot? ParseStats(string output)
    {
        var sections = output.Replace("\r", "").Split(Marker + "\n");
        if (sections.Length < 6) return null;

        try
        {
            var host = sections[0].Trim();

            // cpu  user nice system idle iowait irq softirq steal ...
            var cpu = sections[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(long.Parse).ToArray();
            if (cpu.Length < 5) return null;
            var total = cpu.Take(8).Sum();
            var idle = cpu[3] + cpu[4];

            long memTotal = 0, memAvail = 0, memCached = 0, memBuffers = 0;
            foreach (var line in sections[2].Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                if (parts[0] == "MemTotal:") memTotal = long.Parse(parts[1]);
                else if (parts[0] == "MemAvailable:") memAvail = long.Parse(parts[1]);
                else if (parts[0] == "Cached:") memCached = long.Parse(parts[1]);
                else if (parts[0] == "Buffers:") memBuffers = long.Parse(parts[1]);
            }
            if (memTotal == 0) return null;

            long rx = 0, tx = 0;
            var interfaces = new List<NetInterface>();
            foreach (var line in sections[3].Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = line.IndexOf(':');
                if (colon < 0) continue;
                var name = line[..colon].Trim();
                if (name == "lo") continue;
                var f = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 12) continue;
                // для подсказок показываем все интерфейсы, кроме lo; в общую скорость идут только физические
                interfaces.Add(new NetInterface(name, long.Parse(f[0]), long.Parse(f[8]), long.Parse(f[2]), long.Parse(f[3]), long.Parse(f[10]), long.Parse(f[11])));
                if (name.StartsWith("veth") || name.StartsWith("docker") || name.StartsWith("br-")) continue;
                rx += long.Parse(f[0]);
                tx += long.Parse(f[8]);
            }

            var uptime = double.Parse(sections[4].Trim(), CultureInfo.InvariantCulture);

            var disks = new List<DiskUsage>();
            foreach (var line in sections[5].Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                // Filesystem Type 1K-blocks Used Available Use% Mounted-on
                var f = line.Split(' ', 7, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (f.Length < 7 || !f[5].EndsWith('%')) continue;
                var type = f[1];
                var mount = f[6];
                if (SkipTypes.Contains(type)) continue;
                if (type == "tmpfs" && mount != "/tmp") continue;
                if (SkipMountPrefixes.Any(p => mount == p || mount.StartsWith(p + "/"))) continue;
                disks.Add(new DiskUsage(mount, int.Parse(f[5].TrimEnd('%'))));
            }

            var kernel = sections.Length > 6 ? sections[6].Trim() : null;
            var dfText = sections.Length > 7 ? sections[7].TrimEnd() : null;
            return new Snapshot(host, total, idle, memTotal, memAvail, rx, tx, uptime, disks,
                string.IsNullOrEmpty(kernel) ? null : kernel,
                new MemoryDetails(memTotal, memTotal - memAvail, memAvail, memCached, memBuffers),
                interfaces, string.IsNullOrEmpty(dfText) ? null : dfText);
        }
        catch (FormatException) { return null; }
        catch (OverflowException) { return null; }
    }
}
