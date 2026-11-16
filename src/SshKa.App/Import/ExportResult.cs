namespace SshKa.App.Import;

/// <summary>Результат экспорта: готовый текст файла, счётчик записанных сессий и пропущенные с причиной.</summary>
public sealed class ExportResult
{
    public string Text { get; set; } = "";

    /// <summary>Сколько сессий записано.</summary>
    public int Count { get; set; }

    /// <summary>Пропущенное с причиной, например «srv1 (telnet)».</summary>
    public List<string> Skipped { get; } = new();
}
