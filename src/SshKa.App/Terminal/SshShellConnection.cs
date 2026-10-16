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
        _shell.Closed += (_, _) => RaiseExited();
        _client.ErrorOccurred += (_, _) => RaiseExited();

        // Сервер, ушедший в перезагрузку или пропавший из сети, не всегда закрывает соединение. Пока по нему ничего не
        // передаётся, обрыв незаметен: поэтому раз в 10 секунд отправляем служебное сообщение (оно заодно не даёт
        // сетевым устройствам закрыть простаивающее соединение) и проверяем, что соединение ещё живо.
        _client.KeepAliveInterval = TimeSpan.FromSeconds(10);
        _ = Task.Run(WatchAsync);
    }

    private readonly CancellationTokenSource _watch = new();

    private async Task WatchAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            while (await timer.WaitForNextTickAsync(_watch.Token))
            {
                if (Volatile.Read(ref _exited) != 0) return;
                if (!_client.IsConnected)
                {
                    RaiseExited();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // соединение закрыто нами
        }
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
