using SshKa.App.Localization;
namespace SshKa.App.Files;

/// <summary>Вид файла для значка в проводнике.</summary>
public enum FileKind { Generic, Archive, Image, Video, Audio, Text, Pdf, Code, Script, Config, Link, Binary, Database, Key, Log }

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

    /// <summary>Вид по имени файла (расширению); у каталогов не используется.</summary>
    public FileKind Kind => IsSymlink && !IsDirectory ? FileKind.Link : KindOf(Name);

    /// <summary>Обычный файл без особого значка.</summary>
    public bool IsPlainFile => !IsDirectory && Kind == FileKind.Generic;

    /// <summary>Файл, для которого есть значок по типу.</summary>
    public bool IsTypedFile => !IsDirectory && Kind != FileKind.Generic;

    private static readonly Dictionary<string, FileKind> ByExtension = BuildExtensions();

    private static Dictionary<string, FileKind> BuildExtensions()
    {
        var map = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
        void Add(FileKind kind, string list) { foreach (var e in list.Split(' ')) map[e] = kind; }
        Add(FileKind.Archive, "zip tar gz tgz bz2 xz 7z rar zst lz4 iso deb rpm apk jar war whl vib ova ovf");
        Add(FileKind.Image, "png jpg jpeg gif bmp svg webp ico tif tiff");
        Add(FileKind.Video, "mp4 mkv avi mov wmv webm flv");
        Add(FileKind.Audio, "mp3 wav flac ogg m4a aac opus");
        Add(FileKind.Text, "txt md rst doc docx odt rtf csv tsv readme license");
        Add(FileKind.Pdf, "pdf");
        Add(FileKind.Code, "c h cpp hpp cc cs java py js ts go rs rb php pl lua sql html htm css xml xsl vue jsx tsx kt swift");
        Add(FileKind.Script, "sh bash zsh fish ps1 bat cmd");
        Add(FileKind.Config, "conf cfg ini yaml yml json toml env service timer socket properties rules sudoers");
        Add(FileKind.Binary, "exe dll so bin o a dylib elf ko");
        Add(FileKind.Database, "db sqlite sqlite3 mdb sql3 sqlite-wal");
        Add(FileKind.Key, "pem key crt cer csr pub ppk p12 pfx gpg asc");
        Add(FileKind.Log, "log out err");
        return map;
    }

    private static FileKind KindOf(string name)
    {
        switch (name.ToLowerInvariant())
        {
            case "dockerfile": case "makefile": case "cmakelists.txt": return FileKind.Script;
            case "authorized_keys": case "known_hosts": case "id_rsa": case "id_ed25519": case "id_ecdsa": return FileKind.Key;
            case "readme": case "license": case "copying": case "changelog": return FileKind.Text;
        }

        var dot = name.LastIndexOf('.');
        if (dot > 0 && dot < name.Length - 1 && ByExtension.TryGetValue(name[(dot + 1)..], out var kind)) return kind;
        // скрытые файлы-настройки: .bashrc, .profile, .gitconfig, .env
        if (name.StartsWith('.') && (name.EndsWith("rc") || name.EndsWith("profile") || name.EndsWith("config") || name == ".env" || name.StartsWith(".env.")))
            return FileKind.Config;
        if (name.StartsWith('.') && ByExtension.TryGetValue(name[1..], out kind)) return kind;
        return FileKind.Generic;
    }

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
