using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Porta.Pty;

namespace SshKa.App.Terminal;

/// <summary>
/// Telnet-клиент под IPtyConnection: фильтрует команды IAC из потока данных,
/// отвечает на согласование опций (ECHO, SGA, TTYPE, NAWS), чтобы TerminalControl получал чистый текст.
/// </summary>
public sealed class TelnetConnection : IPtyConnection
{
    private readonly TcpClient _tcp;
    private readonly TelnetStream _stream;
    private int _exited;

    public TelnetConnection(TcpClient tcp, int cols, int rows)
    {
        _tcp = tcp;
        _stream = new TelnetStream(tcp.GetStream(), cols, rows, RaiseExited);
    }

    public static async Task<TelnetConnection> ConnectAsync(string host, int port, int cols, int rows)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(host, port);
            return new TelnetConnection(tcp, cols, rows);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public Stream ReaderStream => _stream;
    public Stream WriterStream => _stream;
    public bool SupportsCancellableRead => true;
    public int Pid => 0;
    public int ExitCode => 0;

    public event EventHandler<PtyExitedEventArgs>? ProcessExited;

    public void Resize(int cols, int rows) => _stream.Resize(cols, rows);

    public bool WaitForExit(int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (Volatile.Read(ref _exited) == 0 && Environment.TickCount64 < deadline)
            Thread.Sleep(20);
        return Volatile.Read(ref _exited) != 0;
    }

    public void Kill()
    {
        try { _tcp.Dispose(); } catch { /* уже закрыт */ }
        RaiseExited();
    }

    public void Dispose() => Kill();

    private void RaiseExited()
    {
        if (Interlocked.Exchange(ref _exited, 1) == 0)
            ProcessExited?.Invoke(this, CreateExitArgs(0));
    }

    // Конструктор PtyExitedEventArgs в Porta.Pty внутренний, публичного способа создать событие нет.
    private static PtyExitedEventArgs CreateExitArgs(int exitCode) =>
        (PtyExitedEventArgs)Activator.CreateInstance(
            typeof(PtyExitedEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null, args: new object[] { exitCode }, culture: null)!;

    private sealed class TelnetStream : Stream
    {
        private const byte IAC = 255, DONT = 254, DO = 253, WONT = 252, WILL = 251, SB = 250, SE = 240;
        private const byte OptEcho = 1, OptSga = 3, OptTtype = 24, OptNaws = 31;
        private const string TerminalType = "xterm-256color";

        private enum State { Data, Iac, Option, Sub, SubIac }

        private readonly NetworkStream _net;
        private readonly Action _onClosed;
        private readonly object _writeLock = new();
        private readonly byte[] _rx = new byte[8192];
        private readonly List<byte> _sub = new();
        private readonly HashSet<byte> _theirs = new(); // опции, которые уже включил сервер
        private readonly HashSet<byte> _ours = new();   // опции, которые уже включили мы

        private State _state = State.Data;
        private byte _command;
        private bool _afterCr;
        private int _cols, _rows;

        public TelnetStream(NetworkStream net, int cols, int rows, Action onClosed)
        {
            _net = net;
            _cols = cols;
            _rows = rows;
            _onClosed = onClosed;
        }

        public void Resize(int cols, int rows)
        {
            _cols = cols;
            _rows = rows;
            if (_ours.Contains(OptNaws)) SendNaws();
        }

        // ---------- чтение: убираем команды, отвечаем на согласование ----------

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            try
            {
                while (true)
                {
                    // читаем не больше размера буфера: данных после фильтрации не станет больше
                    var n = await _net.ReadAsync(_rx.AsMemory(0, Math.Min(_rx.Length, buffer.Length)), ct);
                    if (n == 0)
                    {
                        _onClosed();
                        return 0;
                    }

                    var produced = Filter(_rx.AsSpan(0, n), buffer.Span);
                    if (produced > 0) return produced;
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                _onClosed();
                return 0;
            }
        }

        private int Filter(ReadOnlySpan<byte> input, Span<byte> output)
        {
            var o = 0;
            foreach (var b in input)
            {
                switch (_state)
                {
                    case State.Data:
                        if (b == IAC) { _state = State.Iac; break; }
                        if (b == 0 && _afterCr) { _afterCr = false; break; } // CR NUL означает просто CR
                        _afterCr = b == '\r';
                        output[o++] = b;
                        break;

                    case State.Iac:
                        if (b == IAC) { output[o++] = IAC; _state = State.Data; }
                        else if (b is WILL or WONT or DO or DONT) { _command = b; _state = State.Option; }
                        else if (b == SB) { _sub.Clear(); _state = State.Sub; }
                        else _state = State.Data;
                        break;

                    case State.Option:
                        OnOption(_command, b);
                        _state = State.Data;
                        break;

                    case State.Sub:
                        if (b == IAC) _state = State.SubIac; else _sub.Add(b);
                        break;

                    case State.SubIac:
                        if (b == SE) { OnSubnegotiation(); _state = State.Data; }
                        else if (b == IAC) { _sub.Add(IAC); _state = State.Sub; }
                        else _state = State.Data;
                        break;
                }
            }
            return o;
        }

        private void OnOption(byte command, byte option)
        {
            switch (command)
            {
                case WILL:
                    if (option is OptEcho or OptSga)
                    {
                        if (_theirs.Add(option)) SendCommand(DO, option);
                    }
                    else SendCommand(DONT, option);
                    break;

                case DO:
                    if (option is OptTtype or OptNaws or OptSga)
                    {
                        if (_ours.Add(option))
                        {
                            SendCommand(WILL, option);
                            if (option == OptNaws) SendNaws();
                        }
                    }
                    else SendCommand(WONT, option);
                    break;

                case WONT:
                    _theirs.Remove(option);
                    break;

                case DONT:
                    _ours.Remove(option);
                    break;
            }
        }

        private void OnSubnegotiation()
        {
            // сервер просит тип терминала: IAC SB TTYPE SEND IAC SE
            if (_sub.Count >= 2 && _sub[0] == OptTtype && _sub[1] == 1)
            {
                var reply = new List<byte> { IAC, SB, OptTtype, 0 };
                reply.AddRange(Encoding.ASCII.GetBytes(TerminalType));
                reply.Add(IAC);
                reply.Add(SE);
                SendRaw(reply.ToArray());
            }
        }

        private void SendCommand(byte command, byte option) => SendRaw(new[] { IAC, command, option });

        private void SendNaws()
        {
            var reply = new List<byte> { IAC, SB, OptNaws };
            foreach (var b in new[] { (byte)(_cols >> 8), (byte)_cols, (byte)(_rows >> 8), (byte)_rows })
            {
                reply.Add(b);
                if (b == IAC) reply.Add(IAC); // 255 внутри данных удваивается
            }
            reply.Add(IAC);
            reply.Add(SE);
            SendRaw(reply.ToArray());
        }

        // ---------- запись: экранируем IAC и CR ----------

        private void SendRaw(byte[] bytes)
        {
            try
            {
                lock (_writeLock) _net.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                _onClosed();
            }
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var encoded = new List<byte>(buffer.Length + 4);
            foreach (var b in buffer)
            {
                encoded.Add(b);
                if (b == IAC) encoded.Add(IAC);
                else if (b == '\r') encoded.Add(0); // Enter уходит как CR NUL
            }
            SendRaw(encoded.ToArray());
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        // ---------- остальное для Stream ----------

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _net.Dispose();
            base.Dispose(disposing);
        }
    }
}
