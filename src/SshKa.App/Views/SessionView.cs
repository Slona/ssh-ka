using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Iciclecreek.Terminal;
using SshKa.App.Highlight;
using SshKa.App.Localization;
using SshKa.App.Storage;
using SshKa.App.Terminal;

namespace SshKa.App.Views;

/// <summary>
/// Содержимое SSH-вкладки: терминал и снизу строка состояния сервера.
/// Проводник (<see cref="Files"/>) принадлежит вкладке, но показывается в левой панели окна, когда эта вкладка активна.
/// </summary>
public sealed class SessionView : Grid
{
    private readonly Button _editors = new()
    {
        Classes = { "icon" },
        Content = new PathIcon { Foreground = Avalonia.Media.Brushes.DimGray },
        Margin = new Avalonia.Thickness(2),
    };

    private readonly ServerStatusBar _status = new() { IsVisible = false };
    private SshShellConnection? _conn;
    private ServerStatsCollector? _stats;
    private ShellCwdTracker? _tracker;
    private long _lastRedrawRequest = long.MinValue / 2;

    public TerminalControl Terminal { get; }

    /// <summary>Строка состояния сервера (для проверки внешнего вида).</summary>
    internal ServerStatusBar StatusBar => _status;

    /// <summary>Проводник этой сессии (создаётся сразу, подключается вместе с терминалом).</summary>
    public FilePanel Files { get; } = new();

    public SessionView(TerminalControl terminal)
    {
        Terminal = terminal;

        RowDefinitions.Add(new RowDefinition(GridLength.Star));
        RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        ((PathIcon)_editors.Content!).Data = IconResources.Get("IconPalette");
        _editors.Click += OnSetupEditors;
        ToolTip.SetTip(_editors, Loc.T("Включить подсветку кода в vim и nano на этом сервере"));

        var bar = new Border
        {
            BorderThickness = new Avalonia.Thickness(0, 1, 0, 0),
            BorderBrush = IconResources.Brush("LineBrush"),
            Background = IconResources.Brush("AppBackground"),
            Child = new DockPanel
            {
                LastChildFill = true,
                Children = { _editors, _status },
            },
        };
        DockPanel.SetDock(_editors, Dock.Right);

        var host = TerminalFactory.Host(Terminal);
        _monitoring = AppSettings.Current.ShowServerStatus;
        SetRow(host, 0);
        SetRow(bar, 1);
        Children.Add(host);
        Children.Add(bar);
    }

    /// <summary>Проверяет, включена ли подсветка в vim и nano на сервере, и по подтверждению включает её.</summary>
    private async void OnSetupEditors(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_conn is null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var title = Loc.T("Подсветка в редакторах");
        var server = $"{_conn.Client.ConnectionInfo.Username}@{_conn.Client.ConnectionInfo.Host}";

        EditorSetupPlan plan;
        try { plan = await EditorSetup.InspectAsync(_conn.Client); }
        catch (Exception ex)
        {
            await Dialogs.Info(owner, title, Loc.F("Не удалось проверить сервер: {0}", ex.Message));
            return;
        }

        if (!plan.NeedsVim && !plan.NeedsNano)
        {
            await Dialogs.Info(owner, title, $"{server}\n\n{plan.Describe()}");
            return;
        }

        if (!await Dialogs.Confirm(owner, title, Loc.F("На сервере {0} будут изменены файлы настроек редакторов.", server) + "\n\n" + plan.Describe() + "\n\n" + Loc.T("Продолжить?"), "Включить"))
            return;

        try
        {
            await EditorSetup.ApplyAsync(_conn.Client, plan);
            await Dialogs.Info(owner, title, "Готово. Подсветка заработает при следующем запуске редактора.");
        }
        catch (Exception ex)
        {
            await Dialogs.Info(owner, title, Loc.F("Не удалось записать настройки: {0}", ex.Message));
        }
    }

    public void AttachConnection(SshShellConnection conn)
    {
        _conn = conn;
        var shown = new HighlightingPtyConnection(conn);
        Terminal.AttachConnection(shown);

        // один наблюдатель за терминалом на сессию: каталог для проводника и редактор для подсветки кода
        var tracker = _tracker = new ShellCwdTracker(conn);
        shown.Highlighting.AlternateScreenChanged += entered => { if (entered) tracker.Poke(); };
        tracker.EditorPolled += editor => OnEditorPolled(shown.Highlighting, editor);
        Files.Attach(conn, tracker);
        tracker.Start();

        if (_monitoring) StartStats();
    }

    private bool _monitoring;

    /// <summary>Включает и выключает мониторинг сервера (замеры и панель состояния).</summary>
    public void SetMonitoring(bool on)
    {
        _monitoring = on;
        if (on)
        {
            if (_conn is not null && _stats is null) StartStats();
            return;
        }
        _stats?.Dispose();
        _stats = null;
        _status.IsVisible = false;
    }

    private void StartStats()
    {
        if (_conn is null) return;
        // панель состояния показываем только после первого успешного замера: на не-Linux её не будет вовсе
        var stats = _stats = new ServerStatsCollector(_conn.Client);
        stats.Updated += s => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_stats, stats)) return;
            _status.Update(s);
            _status.IsVisible = true;
        });
        stats.Unsupported += () => Dispatcher.UIThread.Post(() => _status.IsVisible = false);
        stats.Start();
    }

    private void OnEditorPolled(HighlightingStream stream, ForegroundEditor? editor)
    {
        var language = editor is null ? null : CodeLanguages.ForFile(editor.File);
        if (ReferenceEquals(stream.CodeLanguage, language)) return;

        stream.CodeLanguage = language;
        // экран уже нарисован без подсветки — просим редактор перерисоваться (не чаще раза в 2 секунды)
        if (language is not null && editor is not null && stream.IsAlternateScreen
            && Environment.TickCount64 - _lastRedrawRequest > 2000)
        {
            _lastRedrawRequest = Environment.TickCount64;
            _ = _tracker?.RequestRedrawAsync(editor.Pid);
        }
    }

    public void Detach()
    {
        _tracker?.Dispose();
        _stats?.Dispose();
        Files.Detach();
    }
}
