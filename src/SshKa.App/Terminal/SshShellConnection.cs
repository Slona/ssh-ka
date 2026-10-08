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
        try { _shell.Dispose(); } catch { /* уже закрыт */ }
        try { _client.Dispose(); } catch { /* уже закрыт */ }
        RaiseExited();
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
