namespace SshKa.App.Files;

/// <summary>Работа с файлами сервера; реализации отличаются протоколом передачи (SFTP или SCP).</summary>
public interface IRemoteFileSystem : IDisposable
{
    Task ConnectAsync();

    /// <summary>Домашний каталог пользователя.</summary>
    Task<string> HomeAsync();

    Task<IReadOnlyList<RemoteEntry>> ListAsync(string path);

    /// <summary>Скачивает файл или каталог (рекурсивно) в локальную папку localDir.</summary>
    Task DownloadAsync(RemoteEntry entry, string localDir, IProgress<TransferProgress> progress);

    /// <summary>Загружает локальный файл или каталог (рекурсивно) в удалённый каталог remoteDir.</summary>
    Task UploadAsync(string localPath, string remoteDir, IProgress<TransferProgress> progress);

    Task CreateDirectoryAsync(string path);
    Task DeleteAsync(RemoteEntry entry);
    Task RenameAsync(string oldPath, string newPath);

    /// <summary>Меняет права доступа (число вида 0644).</summary>
    Task ChangePermissionsAsync(string path, int mode);
}
