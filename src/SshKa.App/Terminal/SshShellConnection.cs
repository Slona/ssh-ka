using System.Reflection;
using Porta.Pty;
using Renci.SshNet;

namespace SshKa.App.Terminal;

/// <summary>
/// Адаптер SSH-шелла под IPtyConnection, чтобы TerminalControl работал с ним так же, как с локальным PTY.
/// </summary>
public sealed class SshShellConnection : IPtyConnection
{
    private readonly SshClient _client;
    private readonly ShellStream _shell;
    private int _exited;

    public SshShellConnection(SshClient client, int cols, int rows)
    {
        _client = client;
        PtsBefore = ShellCwdTracker.ListPts(client); // снимок до открытия шелла, чтобы потом найти его терминал
        _shell = client.CreateShellStream("xterm-256color", (uint)cols, (uint)rows, 0, 0, 4096);
        _host = client.ConnectionInfo.Host;
        ConnectionLog.Write(_host, "shell opened");
        _shell.Closed += (_, _) => Drop("shell closed by server");
        _client.ErrorOccurred += (_, e) => Drop("connection error: " + e.Exception.GetType().Name + ": " + e.Exception.Message);

        // Сервер, ушедший в перезагрузку или пропавший из сети, не всегда закрывает соединение. Пока по нему ничего не
        // передаётся, обрыв незаметен: поэтому раз в 10 секунд отправляем служебное сообщение (оно заодно не даёт
        // сетевым устройствам закрыть простаивающее соединение) и проверяем, что соединение ещё живо.
        _client.KeepAliveInterval = TimeSpan.FromSeconds(10);
        _ = Task.Run(WatchAsync);
    }

    private readonly CancellationTokenSource _watch = new();
    private readonly string _host;

    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private const int ProbeFailuresToDrop = 2;

    /// <summary>
    /// Следит за тем, что сервер ещё отвечает. Системное keepalive само по себе не помогает: оборванный TCP-канал
    /// Windows считает живым минутами. Поэтому раз в несколько секунд просим сервер выполнить пустую команду
    /// по отдельному каналу: любой ответ (даже отказ) значит «жив», полное молчание два раза подряд — соединение мёртво.
    /// </summary>
    private async Task WatchAsync()
    {
        var silent = 0;
        try
        {
            using var timer = new PeriodicTimer(ProbeInterval);
            while (await timer.WaitForNextTickAsync(_watch.Token))
            {
                if (Volatile.Read(ref _exited) != 0) return;
                if (!_client.IsConnected)
                {
                    Drop("client reports not connected");
                    return;
                }

                var result = await ProbeAsync();
                if (result != Probe.Alive) ConnectionLog.Write(_host, "probe: " + result + " (silent in a row: " + silent + ")");
                SetResponsive(result == Probe.Alive);
                switch (result)
                {
                    case Probe.Alive:
                        silent = 0;
                        break;
                    case Probe.Dead:
                        Drop("probe: connection is dead");
                        return;
                    case Probe.Silent:
                        if (++silent >= ProbeFailuresToDrop)
                        {
                            Drop("server not responding");
                            return;
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // соединение закрыто нами
        }
    }

    private enum Probe { Alive, Silent, Dead }

    private bool _responsive = true;

    /// <summary>Сервер перестал отвечать на проверки (false) или снова отвечает (true): для строки состояния.</summary>
    public event Action<bool>? ResponsivenessChanged;

    private void SetResponsive(bool value)
    {
        if (_responsive == value) return;
        _responsive = value;
        ResponsivenessChanged?.Invoke(value);
    }

    private async Task<Probe> ProbeAsync()
    {
        var work = Task.Run(() =>
        {
            using var cmd = _client.CreateCommand("true");
            cmd.CommandTimeout = ProbeTimeout;
            cmd.Execute();
        });

        try
        {
            if (await Task.WhenAny(work, Task.Delay(ProbeTimeout + TimeSpan.FromSeconds(2))) != work)
            {
                _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // чтобы не было необработанного исключения
                return Probe.Silent;
            }
            await work;
            return Probe.Alive;
        }
        catch (Exception ex)
        {
            ConnectionLog.Write(_host, "probe failed: " + ex.GetType().Name + ": " + ex.Message);
            return ex switch
            {
                Renci.SshNet.Common.SshOperationTimeoutException => Probe.Silent,
                Renci.SshNet.Common.SshConnectionException => Probe.Dead,
                System.Net.Sockets.SocketException => Probe.Dead,
                ObjectDisposedException => Probe.Dead,
                IOException => Probe.Dead,
                // сервер ответил отказом (например, запрещено выполнять команды) — значит, он жив
                _ => Probe.Alive,
            };
        }
    }

    /// <summary>Соединение признано мёртвым: сообщаем об окончании сеанса и закрываем сокет в фоне.</summary>
    private void Drop(string reason)
    {
        ConnectionLog.Write(_host, "session ended: " + reason);
        RaiseExited();
        _ = Task.Run(() =>
        {
            try { _shell.Dispose(); } catch { }
            try { _client.Dispose(); } catch { }
        });
    }

    public SshClient Client => _client;

    /// <summary>Терминалы /dev/pts на сервере, существовавшие до открытия этого шелла.</summary>
    public HashSet<string> PtsBefore { get; }

    public Stream ReaderStream => _shell;
    public Stream WriterStream => _shell;
    public bool SupportsCancellableRead => false;
    public int Pid => 0;
    public int ExitCode { get; private set; }

    public event EventHandler<PtyExitedEventArgs>? ProcessExited;

    public void Resize(int cols, int rows)
    {
        if (cols > 0 && rows > 0)
            _shell.ChangeWindowSize((uint)cols, (uint)rows, 0, 0);
    }

    public bool WaitForExit(int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (Volatile.Read(ref _exited) == 0 && Environment.TickCount64 < deadline)
            Thread.Sleep(20);
        return Volatile.Read(ref _exited) != 0;
    }

    public void Kill()
    {
        // сначала сообщаем об окончании, потом закрываем: закрытие оборванного соединения может занять время
        RaiseExited();
        _watch.Cancel();
        _ = Task.Run(() =>
        {
            try { _shell.Dispose(); } catch { /* уже закрыт */ }
            try { _client.Dispose(); } catch { /* уже закрыт */ }
        });
    }

    public void Dispose() => Kill();

    private void RaiseExited()
    {
        if (Interlocked.Exchange(ref _exited, 1) == 0)
            ProcessExited?.Invoke(this, CreateExitArgs(ExitCode));
    }

    // Конструктор PtyExitedEventArgs в Porta.Pty внутренний, публичного способа создать событие нет.
    private static PtyExitedEventArgs CreateExitArgs(int exitCode) =>
        (PtyExitedEventArgs)Activator.CreateInstance(
            typeof(PtyExitedEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null, args: new object[] { exitCode }, culture: null)!;
}
