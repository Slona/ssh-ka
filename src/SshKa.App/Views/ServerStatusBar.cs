using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using SshKa.App.Localization;
using SshKa.App.Terminal;

namespace SshKa.App.Views;

/// <summary>
/// Нижняя панель SSH-вкладки: имя сервера, CPU с мини-графиком, RAM, скорости сети, аптайм, диски.
/// У каждого показателя свой значок и всплывающая подробность (появляется через полсекунды).
/// </summary>
public sealed class ServerStatusBar : Border
{
    private const int HistoryLength = 30;
    private const double GraphWidth = 54, GraphHeight = 16;
    private const int TipDelayMs = 500;

    private static readonly FontFamily Mono = new("Consolas, Cascadia Mono, DejaVu Sans Mono, monospace");

    private readonly TextBlock _host = Label();
    private readonly TextBlock _cpu = Label();
    private readonly Polyline _graph = new() { Stroke = Brushes.LimeGreen, StrokeThickness = 1.2 };
    private readonly TextBlock _mem = Label();
    private readonly TextBlock _rx = Label();
    private readonly TextBlock _tx = Label();
    private readonly TextBlock _uptime = Label();
    private readonly StackPanel _disks = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
    private readonly Queue<double> _history = new();

    // содержимое подсказок: создаётся один раз и обновляется на месте, чтобы открытая подсказка не мигала
    private readonly TextBlock _hostTip = TipText(), _cpuTip = TipText(), _memTip = TipText(mono: true),
        _txTip = TipText(mono: true), _rxTip = TipText(mono: true), _uptimeTip = TipText(), _diskTip = TipText(mono: true);

    public ServerStatusBar()
    {
        Padding = new Thickness(8, 2);

        var graphBox = new Border
        {
            Width = GraphWidth, Height = GraphHeight, Background = Brushes.Black,
            BorderBrush = Brushes.DimGray, BorderThickness = new Thickness(1),
            Child = new Canvas { Children = { _graph }, ClipToBounds = true },
            VerticalAlignment = VerticalAlignment.Center,
        };

        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                Field("IconMonitor", "#4F5F70", _hostTip, _host),
                Separator(),
                Field("IconChip", "#2E8B3E", _cpuTip, _cpu, graphBox),
                Separator(),
                Field("IconRam", "#2F6DB5", _memTip, _mem),
                Separator(),
                Field("IconArrowUp", "#2E8B3E", _txTip, _tx),
                Field("IconArrowDown", "#2F6DB5", _rxTip, _rx),
                Separator(),
                Field("IconClock", "#6B6B6B", _uptimeTip, _uptime),
                Separator(),
                WithTip(_disks, _diskTip),
            },
        };
    }

    public void Update(ServerStats s)
    {
        _host.Text = s.Hostname;
        _cpu.Text = s.CpuPercent is { } c ? $"{c:0}%" : "…";
        _mem.Text = $"{Gb(s.MemUsedBytes)} / {Gb(s.MemTotalBytes)}";
        _tx.Text = Speed(s.TxBytesPerSec);
        _rx.Text = Speed(s.RxBytesPerSec);
        _uptime.Text = Uptime(s.Uptime);

        if (s.CpuPercent is { } cpu)
        {
            _history.Enqueue(cpu);
            while (_history.Count > HistoryLength) _history.Dequeue();
            DrawGraph();
        }

        _disks.Children.Clear();
        foreach (var d in s.Disks)
        {
            var t = Label();
            t.Text = $"{d.Mount}: {d.UsedPercent}%";
            var bad = d.UsedPercent >= 90;
            if (bad) t.Foreground = Brushes.IndianRed;
            _disks.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children = { Icon("IconDisk", bad ? "#CD5C5C" : "#6B6B6B"), t },
            });
        }

        UpdateTips(s);
    }

    // ---------- подсказки ----------

    private void UpdateTips(ServerStats s)
    {
        SetRows(_hostTip, ("Имя хоста", s.Hostname), ("Версия", s.Kernel ?? "—"));
        _hostTip.MaxWidth = 520;
        _hostTip.TextWrapping = TextWrapping.Wrap;

        SetRows(_cpuTip, ("Текущая загрузка CPU", s.CpuPercent is { } c ? $"{c:0}%" : "…"));

        if (s.Memory is { } m)
        {
            SetRows(_memTip,
                ("Всего RAM", Mb(m.TotalKb)), ("Занято RAM", Mb(m.UsedKb)), ("Доступно RAM", Mb(m.AvailableKb)),
                ("Кэш", Mb(m.CachedKb)), ("Буферы", Mb(m.BuffersKb)));
        }
        else
        {
            SetRows(_memTip, ("Занято RAM", Gb(s.MemUsedBytes)), ("Всего RAM", Gb(s.MemTotalBytes)));
        }

        SetNetwork(_txTip, s, outgoing: true);
        SetNetwork(_rxTip, s, outgoing: false);

        var up = s.Uptime;
        SetRows(_uptimeTip, ("Аптайм", Uptime(up)), ("", Loc.F("({0} секунд)", up.TotalSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))));

        _diskTip.Inlines?.Clear();
        _diskTip.Inlines ??= new InlineCollection();
        _diskTip.Inlines.Add(new Run(Loc.T("Вывод команды «df»:")) { FontWeight = FontWeight.SemiBold, FontFamily = _hostTip.FontFamily });
        _diskTip.Inlines.Add(new LineBreak());
        _diskTip.Inlines.Add(new Run(s.DfText ?? "—"));
    }

    /// <summary>Строки вида «Подпись: значение», подписи полужирным. Пустая подпись — строка без неё.</summary>
    private static void SetRows(TextBlock tip, params (string Label, string Value)[] rows)
    {
        var inlines = tip.Inlines ??= new InlineCollection();
        inlines.Clear();
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0) inlines.Add(new LineBreak());
            if (rows[i].Label.Length > 0)
                inlines.Add(new Run(Loc.T(rows[i].Label) + ": ") { FontWeight = FontWeight.SemiBold });
            inlines.Add(new Run(rows[i].Value));
        }
    }

    private static void SetNetwork(TextBlock tip, ServerStats s, bool outgoing)
    {
        var inlines = tip.Inlines ??= new InlineCollection();
        inlines.Clear();
        if (s.Interfaces is not { Count: > 0 } list)
        {
            inlines.Add(new Run("—"));
            return;
        }

        var first = true;
        foreach (var nic in list)
        {
            if (!first) inlines.Add(new LineBreak());
            first = false;
            var bytes = outgoing ? nic.TxBytes : nic.RxBytes;
            var errors = outgoing ? nic.TxErrors : nic.RxErrors;
            var dropped = outgoing ? nic.TxDropped : nic.RxDropped;
            inlines.Add(new Run($"{nic.Name} {Loc.T(outgoing ? "всего отправлено" : "всего получено")}: {bytes / 1024} kB"));
            inlines.Add(new LineBreak());
            inlines.Add(new Run($"{nic.Name} {Loc.T("ошибок")}: {errors}"));
            inlines.Add(new LineBreak());
            inlines.Add(new Run($"{nic.Name} {Loc.T("потеряно")}: {dropped}"));
        }
    }

    // ---------- оформление ----------

    private static TextBlock TipText(bool mono = false) => new()
    {
        FontFamily = mono ? Mono : new FontFamily("Segoe UI, Noto Sans, DejaVu Sans, Arial"),
        FontSize = mono ? 12 : 13,
    };

    /// <summary>Поле панели: значок, значение и подсказка по наведению.</summary>
    private static Control Field(string icon, string color, TextBlock tip, params Control[] content)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Background = Brushes.Transparent };
        row.Children.Add(Icon(icon, color));
        row.Children.AddRange(content);
        return WithTip(row, tip);
    }

    private static Control WithTip(Control target, TextBlock tip)
    {
        if (target is Panel { Background: null } panel) panel.Background = Brushes.Transparent; // чтобы наведение ловилось и между значками
        ToolTip.SetTip(target, new Border
        {
            Padding = new Thickness(6, 4),
            Child = tip,
        });
        ToolTip.SetShowDelay(target, TipDelayMs);
        ToolTip.SetPlacement(target, PlacementMode.Top);
        return target;
    }

    private static PathIcon Icon(string key, string color) => new()
    {
        Data = IconResources.Get(key),
        Width = 13,
        Height = 13,
        Foreground = new SolidColorBrush(Color.Parse(color)),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void DrawGraph()
    {
        var step = GraphWidth / (HistoryLength - 1);
        var offset = HistoryLength - _history.Count; // график растёт справа налево, как в диспетчере задач
        var points = new List<Point>();
        var i = 0;
        foreach (var v in _history)
            points.Add(new Point((offset + i++) * step, GraphHeight - 1 - v / 100.0 * (GraphHeight - 2)));
        _graph.Points = points;
    }

    private static TextBlock Label() => new() { VerticalAlignment = VerticalAlignment.Center };

    private static Border Separator() => new()
    {
        Width = 1, Height = 14, Background = new SolidColorBrush(Color.Parse("#C6C6C6")), VerticalAlignment = VerticalAlignment.Center,
    };

    private static string Gb(long bytes) => $"{bytes / 1073741824.0:0.00} GB";

    private static string Mb(long kb) => $"{kb / 1024} MB";

    private static string Speed(double? bytesPerSec) => bytesPerSec switch
    {
        null => "…",
        < 1024 => $"{bytesPerSec:0} B/s",
        < 1024 * 1024 => $"{bytesPerSec / 1024:0.0} KB/s",
        _ => $"{bytesPerSec / 1048576:0.00} MB/s",
    };

    private static string Uptime(TimeSpan t) => t.TotalDays >= 1
        ? Loc.F("{0} д. {1} ч.", (int)t.TotalDays, t.Hours)
        : Loc.F("{0} ч. {1} мин.", (int)t.TotalHours, t.Minutes);
}
