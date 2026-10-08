using System.Text;

namespace SshKa.App.Highlight;

/// <summary>
/// Читающая обёртка над потоком вывода сервера: пропускает escape-последовательности как есть,
/// а в обычном тексте раскрашивает совпадения с правилами подсветки.
/// Подсветка не применяется, если сервер сам задал цвет текста, и отключена в полноэкранных программах (vim, htop, less).
/// </summary>
public sealed class HighlightingStream : Stream
{
    private enum Mode { Normal, Esc, Csi, Osc, OscEsc }

    private const int MaxControlStringLength = 4096; // защита от оборванной OSC-последовательности

    private readonly Stream _inner;
    private readonly byte[] _rx = new byte[8192];
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private readonly StringBuilder _run = new();      // накопленный обычный текст
    private readonly StringBuilder _csi = new();      // параметры текущей CSI-последовательности
    private int _controlLength;
    private int _csiStart = -1;                         // где в выходной строке началась текущая CSI-последовательность

    private Mode _mode = Mode.Normal;
    private bool _alternateScreen;
    private bool _serverColor, _serverBold, _serverUnderline, _serverReverse, _serverBackground;

    // редактор в этом сеансе сам выставлял цвет текста — значит, у него своя подсветка, и мы её не дополняем
    private bool _editorHasOwnColors;
    private int _coloredWordRuns;
    private const int OwnColorsThreshold = 3;

    private byte[] _pending = Array.Empty<byte>();
    private int _pendingOffset;

    public HighlightingStream(Stream inner) => _inner = inner;

    /// <summary>
    /// Язык кода, открытого сейчас в редакторе (задаёт тот, кто следит за процессами на сервере).
    /// Действует только в полноэкранном режиме.
    /// </summary>
    public volatile CodeLanguage? CodeLanguage;

    public bool IsAlternateScreen => _alternateScreen;

    /// <summary>Срабатывает при входе в полноэкранный режим (true) и выходе из него (false), из потока чтения.</summary>
    public event Action<bool>? AlternateScreenChanged;

    // ---------- чтение ----------

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (_pendingOffset >= _pending.Length)
        {
            var n = _inner.Read(_rx, 0, _rx.Length);
            if (n == 0) return 0;
            Fill(_rx.AsSpan(0, n));
        }
        return Drain(buffer.AsSpan(offset, count));
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (_pendingOffset >= _pending.Length)
        {
            var n = await _inner.ReadAsync(_rx.AsMemory(), ct);
            if (n == 0) return 0;
            Fill(_rx.AsSpan(0, n));
        }
        return Drain(buffer.Span);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    private int Drain(Span<byte> destination)
    {
        // подсветка увеличивает объём данных, поэтому остаток отдаём в следующих чтениях
        var n = Math.Min(destination.Length, _pending.Length - _pendingOffset);
        _pending.AsSpan(_pendingOffset, n).CopyTo(destination);
        _pendingOffset += n;
        return n;
    }

    private void Fill(ReadOnlySpan<byte> input)
    {
        var chars = new char[_decoder.GetCharCount(input, false)];
        var count = _decoder.GetChars(input, chars, false);
        var text = Transform(chars.AsSpan(0, count));
        _pending = Encoding.UTF8.GetBytes(text);
        _pendingOffset = 0;
    }

    // ---------- разбор потока ----------

    private string Transform(ReadOnlySpan<char> input)
    {
        var sb = new StringBuilder(input.Length + 32);
        if (_mode != Mode.Normal) _csiStart = -1; // последовательность началась в прошлом блоке и уже отдана

        foreach (var c in input)
        {
            switch (_mode)
            {
                case Mode.Normal:
                    if (c == '\u001b')
                    {
                        FlushRun(sb);
                        sb.Append(c);
                        _mode = Mode.Esc;
                    }
                    else _run.Append(c);
                    break;

                case Mode.Esc:
                    sb.Append(c);
                    _controlLength = 0;
                    if (c == '[') { _mode = Mode.Csi; _csi.Clear(); _csiStart = sb.Length >= 2 ? sb.Length - 2 : -1; }
                    else if (c is ']' or 'P' or '_' or '^' or 'X') _mode = Mode.Osc; // OSC, DCS, APC, PM, SOS
                    else _mode = Mode.Normal;
                    break;

                case Mode.Csi:
                    sb.Append(c);
                    if (c is >= '@' and <= '~')
                    {
                        if (c == 'm' && _csiStart >= 0 && !Storage.AppSettings.Current.TerminalBold)
                            DropBold(sb);
                        EndCsi(c);
                        _mode = Mode.Normal;
                    }
                    else if (++_controlLength > MaxControlStringLength) _mode = Mode.Normal;
                    else _csi.Append(c);
                    break;

                case Mode.Osc:
                    sb.Append(c);
                    if (c == '\a') _mode = Mode.Normal;
                    else if (c == '\u001b') _mode = Mode.OscEsc;
                    else if (++_controlLength > MaxControlStringLength) _mode = Mode.Normal;
                    break;

                case Mode.OscEsc:
                    sb.Append(c);
                    _mode = c == '\\' ? Mode.Normal : Mode.Osc;
                    break;
            }
        }

        // конец блока: выдаём накопленный текст, не дожидаясь следующего (эхо набираемых символов не должно задерживаться)
        if (_mode == Mode.Normal) FlushRun(sb);
        return sb.ToString();
    }

    private void FlushRun(StringBuilder output)
    {
        if (_run.Length == 0) return;
        var text = _run.ToString();
        _run.Clear();

        if (!HighlightEngine.Enabled || _serverColor)
        {
            // редактор красит слова сам, значит, у него своя подсветка синтаксиса. Одних цветных символов-заполнителей
            // (синие ~ у пустых строк vim) для этого мало, поэтому считаем куски, где есть буквы.
            if (_alternateScreen && _serverColor && !_editorHasOwnColors
                && text.Any(char.IsLetter) && ++_coloredWordRuns >= OwnColorsThreshold)
                _editorHasOwnColors = true;
            output.Append(text);
            return;
        }

        if (_alternateScreen)
        {
            // в редакторе красим код, но не строки состояния и меню: они выделены реверсом или фоном
            var language = CodeLanguage;
            var code = HighlightEngine.CodeEnabled && language is not null
                && !_editorHasOwnColors && !_serverReverse && !_serverBackground;
            output.Append(code ? HighlightEngine.ApplyCode(text, language!) : text);
            return;
        }

        output.Append(HighlightEngine.Apply(text, _serverBold, _serverUnderline));
    }

    /// <summary>
    /// Без жирного начертания: «жирный» от сервера (SGR 1) заменяется яркой версией цвета, как в PuTTY.
    /// Последовательность уже лежит в sb, поэтому переписываем её с отметки _csiStart.
    /// </summary>
    private void DropBold(StringBuilder sb)
    {
        var parameters = _csi.ToString();
        if (parameters.StartsWith('?') || parameters.StartsWith('>') || parameters.StartsWith('<')) return;

        var parts = parameters.Length == 0 ? new[] { "0" } : parameters.Split(';');
        var bold = _serverBold;
        var hasBold = false;
        for (var i = 0; i < parts.Length; i++)
        {
            var code = parts[i].Split(':')[0]; if (int.TryParse(code, out var codeNumber)) code = codeNumber.ToString(); // "01" = "1"
            if (code is "38" or "48") { i += i + 1 < parts.Length && parts[i + 1] == "5" ? 2 : 4; continue; }
            if (code == "1") { bold = true; hasBold = true; }
            else if (code is "0" or "22") bold = false;
        }
        if (!hasBold && !bold) return; // ни жирного, ни его следов — оставляем как есть

        // жирность нужно знать на момент каждого цвета: идём по порядку, начиная с состояния до этой последовательности
        bold = _serverBold || hasBold;
        var result = new List<string>();
        for (var i = 0; i < parts.Length; i++)
        {
            var code = parts[i].Split(':')[0]; if (int.TryParse(code, out var codeNumber)) code = codeNumber.ToString(); // "01" = "1"
            if (code is "38" or "48")
            {
                var take = i + 1 < parts.Length && parts[i + 1] == "5" ? 3 : 5;
                for (var k = 0; k < take && i + k < parts.Length; k++) result.Add(parts[i + k]);
                i += take - 1;
                continue;
            }
            if (code == "1") continue;
            if (code is "0" or "22") bold = false;
            if (bold && int.TryParse(code, out var n) && n is >= 30 and <= 37) result.Add((n + 60).ToString());
            else result.Add(parts[i]);
        }

        sb.Length = _csiStart;
        if (result.Count > 0) sb.Append((char)27).Append('[').Append(string.Join(';', result)).Append('m');
    }

    private void EndCsi(char final)
    {
        var parameters = _csi.ToString();

        if (final == 'm' && !parameters.StartsWith('?') && !parameters.StartsWith('>'))
        {
            TrackSgr(parameters);
        }
        else if (final is 'h' or 'l' && parameters.StartsWith('?'))
        {
            // 47, 1047, 1049 — переключение на альтернативный экран полноэкранных программ
            foreach (var p in parameters[1..].Split(';'))
            {
                if (p is not ("47" or "1047" or "1049")) continue;
                var entered = final == 'h';
                if (entered == _alternateScreen) continue;

                // язык здесь не сбрасываем: nano на перерисовку сам выходит из полноэкранного режима и входит снова,
                // а актуальность языка проверяет опрос процессов, который запускается по этому событию
                _alternateScreen = entered;
                if (entered) { _editorHasOwnColors = false; _coloredWordRuns = 0; } // для новой программы выясняем заново
                AlternateScreenChanged?.Invoke(entered);
            }
        }
    }

    /// <summary>Следит за цветом и начертанием, которые выставил сам сервер, чтобы не ломать их.</summary>
    private void TrackSgr(string parameters)
    {
        if (parameters.Length == 0) { _serverColor = _serverBold = _serverUnderline = _serverReverse = _serverBackground = false; return; }

        var parts = parameters.Split(';');
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i].Split(':')[0], out var code)) code = 0;
            switch (code)
            {
                case 0: _serverColor = _serverBold = _serverUnderline = _serverReverse = _serverBackground = false; break;
                case 7: _serverReverse = true; break;
                case 27: _serverReverse = false; break;
                case 49: _serverBackground = false; break;
                case >= 40 and <= 47: _serverBackground = true; break;
                case >= 100 and <= 107: _serverBackground = true; break;
                case 1: _serverBold = true; break;
                case 22: _serverBold = false; break;
                case 4: _serverUnderline = true; break;
                case 24: _serverUnderline = false; break;
                case 39: _serverColor = false; break;
                case >= 30 and <= 37: _serverColor = true; break;
                case >= 90 and <= 97: _serverColor = true; break;
                case 38:
                    _serverColor = true;
                    // 38;5;N или 38;2;R;G;B — пропускаем аргументы цвета
                    if (i + 1 < parts.Length && parts[i + 1] == "5") i += 2;
                    else if (i + 1 < parts.Length && parts[i + 1] == "2") i += 4;
                    break;
                case 48:
                    _serverBackground = true;
                    if (i + 1 < parts.Length && parts[i + 1] == "5") i += 2;
                    else if (i + 1 < parts.Length && parts[i + 1] == "2") i += 4;
                    break;
            }
        }
    }

    // ---------- остальное для Stream ----------

    public override bool CanRead => true;
    public override bool CanWrite => false;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
