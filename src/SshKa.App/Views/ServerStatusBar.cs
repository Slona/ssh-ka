using SshKa.App.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using SshKa.App.Terminal;

namespace SshKa.App.Views;

/// <summary>Нижняя панель SSH-вкладки: имя сервера, CPU с мини-графиком, RAM, скорости сети, аптайм, диски.</summary>
public sealed class ServerStatusBar : Border
{
    private const int HistoryLength = 30;
    private const double GraphWidth = 54, GraphHeight = 16;

    private readonly TextBlock _host = Label();
    private readonly TextBlock _cpu = Label();
    private readonly Polyline _graph = new() { Stroke = Brushes.LimeGreen, StrokeThickness = 1.2 };
    private readonly TextBlock _mem = Label();
    private readonly TextBlock _rx = Label();
    private readonly TextBlock _tx = Label();
    private readonly TextBlock _uptime = Label();
    private readonly StackPanel _disks = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
    private readonly Queue<double> _history = new();

    public ServerStatusBar()
    {
        Padding = new Thickness(8, 2);

        var graphBox = new Border
        {
            Width = GraphWidth, Height = GraphHeight, Background = Brushes.Black,
            BorderBrush = Brushes.DimGray, BorderThickness = new Thickness(1),
            Child = new Canvas { Children = { _graph }, ClipToBounds = true },
        };

        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                _host,
                Separator(),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _cpu, graphBox } },
                Separator(),
                _mem,
                Separator(),
                _tx,
                _rx,
                Separator(),
                _uptime,
                Separator(),
                _disks,
            },
        };
    }

    public void Update(ServerStats s)
    {
        _host.Text = s.Hostname;
        _cpu.Text = s.CpuPercent is { } c ? $"{c:0}%" : "…";
        _mem.Text = $"{Gb(s.MemUsedBytes)} / {Gb(s.MemTotalBytes)}";
        _tx.Text = "↑ " + Speed(s.TxBytesPerSec);
        _rx.Text = "↓ " + Speed(s.RxBytesPerSec);
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
            if (d.UsedPercent >= 90) t.Foreground = Brushes.IndianRed;
            _disks.Children.Add(t);
        }
    }

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
