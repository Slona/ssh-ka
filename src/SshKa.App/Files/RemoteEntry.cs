using SshKa.App.Localization;
namespace SshKa.App.Files;

public sealed class RemoteEntry
{
    public RemoteEntry(string name, string fullPath, bool isDirectory, bool isSymlink, long size, DateTime? modified,
        string? owner = null, string? group = null, int mode = -1)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        IsSymlink = isSymlink;
        Size = size;
        Modified = modified;
        Owner = owner ?? "";
        Group = group ?? "";
        Mode = mode;
    }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public bool IsSymlink { get; }
    public long Size { get; }
    public DateTime? Modified { get; }
    public string Owner { get; }
    public string Group { get; }

    /// <summary>Права доступа (биты 0..07777); -1, если сервер их не сообщил.</summary>
    public int Mode { get; }

    public string SizeText => IsDirectory ? "" : FormatSize(Size);
    public string DateText => Modified?.ToString("dd.MM.yy HH:mm") ?? "";
    public string PermissionsText => Mode < 0 ? "" : PermissionBits.ToText(Mode, IsDirectory, IsSymlink);

    private static string FormatSize(long bytes)
    {
        string[] units = { Loc.T("Б"), Loc.T("КБ"), Loc.T("МБ"), Loc.T("ГБ"), Loc.T("ТБ") };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} {units[0]}" : $"{v:0.#} {units[i]}";
    }
}

public readonly record struct TransferProgress(string Name, long Done, long Total);
