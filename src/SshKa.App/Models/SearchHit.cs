namespace SshKa.App.Models;

/// <summary>Найденная сессия и путь к ней (папки через « / ») для списка результатов поиска.</summary>
public sealed record SearchHit(SessionItem Item, string Path)
{
    public string PathText => Path.Length == 0 ? "" : "— " + Path;
}
