using System.Text.Json;
using System.Text.Json.Serialization;

namespace SshKa.App.Storage;

public enum TransferProtocol { Sftp, Scp }

/// <summary>Настройки интерфейса. Секретов здесь нет, поэтому файл не шифруется.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SshKa", "settings.json");

    public static AppSettings Current { get; } = Load();

    public TransferProtocol Transfer { get; set; } = TransferProtocol.Sftp;
    public bool ShowFilePanel { get; set; } = true;
    public bool FollowTerminal { get; set; } = true;
    public string? LastDownloadDir { get; set; }

    /// <summary>Шрифт терминала: простой моноширинный. Меняется правкой settings.json.</summary>
    public string TerminalFontFamily { get; set; } = "Consolas, Cascadia Mono, DejaVu Sans Mono, monospace";

    public double TerminalFontSize { get; set; } = 14;

    /// <summary>Жирное начертание в терминале. Выключено: «жирный» от сервера показывается яркими цветами, как в PuTTY.</summary>
    public bool TerminalBold { get; set; }

    /// <summary>Выделенный в терминале текст сразу попадает в буфер обмена (как в PuTTY).</summary>
    public bool CopyOnSelect { get; set; } = true;

    /// <summary>Язык интерфейса: "ru" или "en". По умолчанию язык системы: русский, иначе английский. Применяется после перезапуска.</summary>
    public string Language { get; set; } = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";

    /// <summary>Спрашивать мастер-пароль при каждом запуске. По умолчанию нет: ключ запоминается на этом компьютере (DPAPI).</summary>
    public bool AskMasterPassword { get; set; }

    /// <summary>Показывать панель состояния сервера под терминалом.</summary>
    public bool ShowServerStatus { get; set; } = true;

    /// <summary>Версия формата файла: нужна, чтобы один раз обновить прежние значения по умолчанию.</summary>
    public int Version { get; set; }

    public bool HighlightEnabled { get; set; } = true;

    /// <summary>Раскрашивать код в редакторах (vim, nano…) силами клиента, когда сам редактор цвета не задаёт.</summary>
    public bool HighlightCodeInEditors { get; set; } = true;

    /// <summary>Правила подсветки; null — ещё не менялись, действуют правила по умолчанию.</summary>
    public List<Highlight.HighlightRule>? HighlightRules { get; set; }

    [JsonIgnore]
    public List<Highlight.HighlightRule> EffectiveHighlightRules => HighlightRules ??= Highlight.HighlightRule.Defaults();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
                if (loaded.Version < 2)
                {
                    loaded.TerminalFontSize = 14; // шрифт чуть крупнее прежних 13
                    loaded.Version = 2;
                }
                return loaded;
            }
        }
        catch
        {
            // битый файл настроек не должен мешать запуску
        }
        return new() { Version = 2 };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch
        {
            // настройки не критичны
        }
    }
}
