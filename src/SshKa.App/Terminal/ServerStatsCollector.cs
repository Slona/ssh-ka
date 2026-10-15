using System.Diagnostics;
using System.Globalization;
using Renci.SshNet;

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

/// <summary>
/// Раз в пару секунд выполняет на сервере один короткий скрипт (читает /proc и df) по отдельному exec-каналу,
/// не затрагивая терминал. Рассчитан на Linux: если /proc недоступен, сообщает об этом через Unsupported.
/// </summary>
public sealed class ServerStatsCollector : IDisposable
{
    private const string Marker = "@@@";
    private const int MaxConsecutiveFailures = 5;

    // Один вызов — все данные. Разделитель между секциями — строка @@@.
    private const string Script =
        "hostname; echo @@@; head -n1 /proc/stat; echo @@@; " +
        "grep -E '^(MemTotal|MemAvailable|Cached|Buffers):' /proc/meminfo; echo @@@; " +
        "cat /proc/net/dev; echo @@@; cut -d' ' -f1 /proc/uptime; echo @@@; " +
        "df -PkT 2>/dev/null; echo @@@; cat /proc/version 2>/dev/null; echo @@@; df -h 2>/dev/null";

    private readonly SshClient _client;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();

    private Snapshot? _prev;
    private long _prevTicks;

    public event Action<ServerStats>? Updated;
    public event Action? Unsupported;

    public ServerStatsCollector(SshClient client, TimeSpan? interval = null)
    {
        _client = client;
        _interval = interval ?? TimeSpan.FromSeconds(2);
    }

    public void Start() => _ = Task.Run(LoopAsync);

    public void Dispose() => _cts.Cancel();

    private async Task LoopAsync()
    {
        var ct = _cts.Token;
        var failures = 0;
        while (!ct.IsCancellationRequested && _client.IsConnected)
        {
            try
            {
                using var cmd = _client.CreateCommand(Script);
                var output = cmd.Execute();
                var snap = Parse(output);
                if (snap is null) throw new FormatException("нет данных /proc");

                failures = 0;
                Updated?.Invoke(Compute(snap));
            }
            catch
            {
                if (++failures >= MaxConsecutiveFailures)
                {
                    Unsupported?.Invoke();
                    return;
                }
            }

            try { await Task.Delay(_interval, ct); }
            catch (OperationCanceledException) { return; }
        }
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

    internal static Snapshot? Parse(string output)
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
                if (name == "lo" || name.StartsWith("veth") || name.StartsWith("docker") || name.StartsWith("br-")) continue;
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
