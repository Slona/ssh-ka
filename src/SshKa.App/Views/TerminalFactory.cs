using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
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
        };
        ApplySettings(terminal);

        // как в PuTTY: выделил мышью — текст уже в буфере обмена
        terminal.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased,
            RoutingStrategies.Bubble | RoutingStrategies.Tunnel, handledEventsToo: true);
        // правая кнопка вставляет из буфера обмена, как в PuTTY
        terminal.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        return terminal;
    }

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TerminalControl terminal) return;
        if (!e.GetCurrentPoint(terminal).Properties.IsRightButtonPressed) return;
        e.Handled = true;
        _ = terminal.PasteAsync();
    }

    /// <summary>Подставляет текущие настройки шрифта (вызывается и для уже открытых терминалов).</summary>
    public static void ApplySettings(TerminalControl terminal)
    {
        var settings = AppSettings.Current;
        terminal.FontFamily = new FontFamily(settings.TerminalFontFamily);
        terminal.FontSize = settings.TerminalFontSize;
        terminal.FontWeight = FontWeight.Normal;
        if (terminal.Parent is Border border) border.Padding = EdgeOf(settings.TerminalFontSize);
    }

    /// <summary>Терминал в рамке с отступом слева примерно в полсимвола; фон рамки совпадает с фоном терминала.</summary>
    public static Border Host(TerminalControl terminal) => new()
    {
        Background = Back,
        Padding = EdgeOf(AppSettings.Current.TerminalFontSize),
        Child = terminal,
    };

    private static Thickness EdgeOf(double fontSize) => new(Math.Round(fontSize * 0.28), 0, 0, 0);

    private static void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not TerminalControl terminal || !AppSettings.Current.CopyOnSelect) return;
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        // CopyAsync копирует только непустое выделение; обычный щелчок буфер не трогает
        _ = terminal.CopyAsync();
    }
}
