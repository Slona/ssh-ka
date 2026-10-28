using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Iciclecreek.Terminal;
using SshKa.App.Storage;

namespace SshKa.App.Views;

/// <summary>Создание и настройка терминалов: шрифт, отступ от края, копирование выделенного.</summary>
public static class TerminalFactory
{
    private static readonly IBrush Back = new SolidColorBrush(Color.Parse("#0C0C0C"));

    /// <param name="process">Локальная программа; пустая строка — терминал без своего процесса (SSH, Telnet).</param>
    public static TerminalControl Create(string process = "")
    {
        var terminal = new TerminalControl
        {
            Background = Back,
            // Контрол сам запускает локальную программу (по умолчанию cmd.exe) сразу, как появится на экране.
            // Для SSH и Telnet это лишний процесс, поэтому пустая строка; для Local запуск делает сам контрол.
            Process = process,
            // над окном терминала указатель мыши — текстовый (I-beam), так удобнее выделять
            Cursor = new Cursor(StandardCursorType.Ibeam),
            // курсор-маркер, как в MobaXterm: блок без мигания
            CursorStyle = XTerm.Common.CursorStyle.Block,
            CursorBlink = false,
        };
        ApplySettings(terminal);

        // как в PuTTY: выделил мышью — текст уже в буфере обмена
        terminal.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased,
            RoutingStrategies.Bubble | RoutingStrategies.Tunnel, handledEventsToo: true);
        // правая кнопка вставляет из буфера обмена, как в PuTTY
        terminal.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        // Esc контрол отправляет в формате win32-input-mode (CSI 27;0;27;1;32;1_), которого сервер не понимает: в vi вместо
        // выхода из режима вставки появлялись символы "__". Отправляем обычный ESC сами.
        terminal.AddHandler(InputElement.KeyDownEvent, OnEscapeKey, RoutingStrategies.Tunnel);
        terminal.AddHandler(InputElement.KeyUpEvent, OnEscapeKey, RoutingStrategies.Tunnel);
        return terminal;
    }

    private static void OnEscapeKey(object? sender, KeyEventArgs e)
    {
        if (sender is not TerminalControl terminal || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None) return;
        e.Handled = true;
        if (e.RoutedEvent == InputElement.KeyDownEvent) _ = terminal.SendInputAsync("");
    }

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TerminalControl terminal) return;
        var point = e.GetCurrentPoint(terminal);
        if (point.Properties.IsLeftButtonPressed)
        {
            _pressAt = point.Position;
            _pressClicks = e.ClickCount;
        }
        if (!point.Properties.IsRightButtonPressed) return;
        e.Handled = true;
        _ = PasteAsync(terminal);
    }

    /// <summary>
    /// Вставка из буфера обмена. Буфер Windows может быть на мгновение занят другой программой (или нашим же
    /// копированием выделенного), поэтому при сбое пробуем ещё раз, а в конце вставляем текст напрямую.
    /// Причина сбоя пишется в paste.log рядом с настройками.
    /// </summary>
    private static async Task PasteAsync(TerminalControl terminal)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                await terminal.PasteAsync();
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(60);
            }
        }

        try
        {
            var clipboard = TopLevel.GetTopLevel(terminal)?.Clipboard;
            var text = clipboard is null ? null : await clipboard.TryGetTextAsync();
            if (!string.IsNullOrEmpty(text)) await terminal.SendInputAsync(text.Replace("\r\n", "\r").Replace('\n', '\r'));
        }
        catch (Exception ex)
        {
            last = ex;
        }
        LogPasteFailure(last);
    }

    private static void LogPasteFailure(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SshKa", "paste.log");
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}{Environment.NewLine}");
        }
        catch
        {
            // журнал нужен только для разбора, его отсутствие не должно ничего ломать
        }
    }

    public const string BundledFont = "Kibernetyk Mono";
    private const string BundledFontUri = "avares://SshKa.App/Assets/Fonts#" + BundledFont;

    /// <summary>Шрифт из списка настроек; встроенный Kibernetyk Mono подключается из ресурсов программы.</summary>
    public static FontFamily ResolveFont(string families)
    {
        var parts = families.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Equals(BundledFont, StringComparison.OrdinalIgnoreCase) ? BundledFontUri : f);
        return new FontFamily(string.Join(", ", parts));
    }

    /// <summary>Подставляет текущие настройки шрифта (вызывается и для уже открытых терминалов).</summary>
    public static void ApplySettings(TerminalControl terminal)
    {
        var settings = AppSettings.Current;
        terminal.FontFamily = ResolveFont(settings.TerminalFontFamily);
        terminal.FontSize = settings.TerminalFontSize;
        terminal.FontWeight = FontWeight.Normal;
        if (terminal.Parent is Grid { Parent: Border border }) border.Padding = EdgeOf(settings.TerminalFontSize);
    }

    /// <summary>Терминал в рамке с отступом слева примерно в полсимвола; фон рамки совпадает с фоном терминала.</summary>
    public static Border Host(TerminalControl terminal)
    {
        // пустой маркер на месте курсора, пока окно или терминал неактивны (как в MobaXterm)
        var outline = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.Parse("#D8D8D8")),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            IsVisible = false,
        };
        var grid = new Grid { Children = { terminal, outline } };
        var host = new Border
        {
            Background = Back,
            Padding = EdgeOf(AppSettings.Current.TerminalFontSize),
            Child = grid,
        };

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) => UpdateCursor(terminal, outline);
        host.AttachedToVisualTree += (_, _) => timer.Start();
        host.DetachedFromVisualTree += (_, _) => timer.Stop();
        return host;
    }

    /// <summary>Для проверки внешнего вида: считать терминал неактивным.</summary>
    internal static bool ForceInactive;

    /// <summary>Активный терминал показывает залитый курсор-блок, неактивный (окно в фоне или фокус в другом месте) — пустой контур.</summary>
    internal static void UpdateCursor(TerminalControl terminal, Border outline)
    {
        var window = TopLevel.GetTopLevel(terminal) as Window;
        var active = !ForceInactive && window is { IsActive: true } && terminal.IsKeyboardFocusWithin;
        try
        {
            terminal.SuppressCursor = !active;
            if (active || terminal.CharWidth <= 0 || terminal.CharHeight <= 0)
            {
                outline.IsVisible = false;
                return;
            }

            var buffer = terminal.Terminal.Buffer;
            var row = buffer.YBase + buffer.Y - buffer.YDisp;
            outline.Width = terminal.CharWidth;
            outline.Height = terminal.CharHeight;
            outline.Margin = new Thickness(buffer.X * terminal.CharWidth + terminal.GutterWidth, row * terminal.CharHeight, 0, 0);
            outline.IsVisible = row >= 0 && row < terminal.Terminal.Rows;
        }
        catch
        {
            outline.IsVisible = false; // терминал ещё не готов
        }
    }

    private static Thickness EdgeOf(double fontSize) => new(Math.Round(fontSize * 0.28), 0, 0, 0);

    // Щелчок, которым окно выводится на передний план, часто сдвигает мышь на пару пикселей и выделяет один символ,
    // а копирование на выделение затирало буфер обмена. Одиночный щелчок почти без движения выделением не считаем.
    private static Point _pressAt;
    private static int _pressClicks;

    private static void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not TerminalControl terminal || !AppSettings.Current.CopyOnSelect) return;
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (_pressClicks <= 1)
        {
            var moved = e.GetPosition(terminal) - _pressAt;
            var threshold = Math.Max(terminal.CharWidth, 8);
            if (Math.Abs(moved.X) < threshold && Math.Abs(moved.Y) < terminal.CharHeight / 2) return;
        }
        // CopyAsync копирует только непустое выделение; обычный щелчок буфер не трогает
        _ = terminal.CopyAsync();
    }
}
