using SshKa.App.Models;

namespace SshKa.App.Import;

/// <summary>Результат импорта: дерево узлов верхнего уровня и перечень того, что перенести не удалось.</summary>
public sealed class ImportResult
{
    public List<SessionItem> Items { get; } = new();

    /// <summary>Сколько сессий (не папок) импортировано.</summary>
    public int SessionCount { get; set; }

    /// <summary>Пропущенное с причиной, например «srv1 (RDP)».</summary>
    public List<string> Skipped { get; } = new();

    /// <summary>Находит или создаёт вложенную папку по пути из имён и возвращает её содержимое.</summary>
    public static IList<SessionItem> FolderContents(IList<SessionItem> root, IReadOnlyList<string> path)
    {
        var level = root;
        foreach (var name in path)
        {
            var folder = level.FirstOrDefault(i => i.IsFolder && i.Name == name);
            if (folder is null)
            {
                folder = new SessionItem { IsFolder = true, Name = name };
                level.Add(folder);
            }
            level = folder.Children;
        }
        return level;
    }
}
