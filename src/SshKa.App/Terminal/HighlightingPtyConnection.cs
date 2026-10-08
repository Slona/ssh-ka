using Porta.Pty;
using SshKa.App.Highlight;

namespace SshKa.App.Terminal;

/// <summary>Оборачивает соединение так, чтобы вывод сервера проходил через подсветку; остальное передаёт как есть.</summary>
public sealed class HighlightingPtyConnection : IPtyConnection
{
    private readonly IPtyConnection _inner;

    public HighlightingPtyConnection(IPtyConnection inner)
    {
        _inner = inner;
        Highlighting = new HighlightingStream(inner.ReaderStream);
    }

    /// <summary>Поток подсветки: через него задаётся язык кода и приходят сигналы о полноэкранных программах.</summary>
    public HighlightingStream Highlighting { get; }

    public Stream ReaderStream => Highlighting;
    public Stream WriterStream => _inner.WriterStream;
    public bool SupportsCancellableRead => _inner.SupportsCancellableRead;
    public int Pid => _inner.Pid;
    public int ExitCode => _inner.ExitCode;

    public event EventHandler<PtyExitedEventArgs>? ProcessExited
    {
        add => _inner.ProcessExited += value;
        remove => _inner.ProcessExited -= value;
    }

    public void Resize(int cols, int rows) => _inner.Resize(cols, rows);
    public bool WaitForExit(int milliseconds) => _inner.WaitForExit(milliseconds);
    public void Kill() => _inner.Kill();
    public void Dispose() => _inner.Dispose();
}
