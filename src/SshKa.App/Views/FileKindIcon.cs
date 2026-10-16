using Avalonia.Data.Converters;
using Avalonia.Media;
using SshKa.App.Files;

namespace SshKa.App.Views;

/// <summary>Значок и цвет файла в проводнике по его виду.</summary>
public static class FileKindIcon
{
    private static string Key(FileKind kind) => kind switch
    {
        FileKind.Archive => "IconArchive",
        FileKind.Image => "IconImage",
        FileKind.Video => "IconVideo",
        FileKind.Audio => "IconAudio",
        FileKind.Text => "IconDocument",
        FileKind.Pdf => "IconDocument",
        FileKind.Log => "IconDocument",
        FileKind.Code => "IconCode",
        FileKind.Script => "IconScript",
        FileKind.Config => "IconGear",
        FileKind.Link => "IconLink",
        FileKind.Binary => "IconChip",
        FileKind.Database => "IconDatabase",
        FileKind.Key => "IconKey",
        _ => "IconFile",
    };

    private static string Color(FileKind kind) => kind switch
    {
        FileKind.Archive => "#B7791F",
        FileKind.Image => "#2E9E5B",
        FileKind.Video => "#8E44AD",
        FileKind.Audio => "#C2185B",
        FileKind.Text => "#5A6B7B",
        FileKind.Pdf => "#C0392B",
        FileKind.Log => "#8A8A8A",
        FileKind.Code => "#2F6DB5",
        FileKind.Script => "#2E8B3E",
        FileKind.Config => "#6B6B6B",
        FileKind.Link => "#2F6DB5",
        FileKind.Binary => "#6B6B6B",
        FileKind.Database => "#8E5A2B",
        FileKind.Key => "#C8921A",
        _ => "#707070",
    };

    public static readonly IValueConverter Geometry = new FuncValueConverter<FileKind, Avalonia.Media.Geometry?>(k => IconResources.Get(Key(k)));

    public static readonly IValueConverter Brush = new FuncValueConverter<FileKind, IBrush?>(k => new SolidColorBrush(Avalonia.Media.Color.Parse(Color(k))));
}
