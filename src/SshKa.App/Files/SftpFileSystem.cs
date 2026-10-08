using SshKa.App.Localization;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using SshKa.App.Storage;

namespace SshKa.App.Files;

public sealed class SftpFileSystem : IRemoteFileSystem
{
    private readonly SftpClient _client;
    private readonly SshClient? _exec;
    private Dictionary<int, string>? _users, _groups;

    /// <param name="exec">Уже открытое SSH-соединение: по нему один раз узнаём имена владельцев по числовым id (getent).</param>
    public SftpFileSystem(ConnectionInfo info, SshClient? exec = null)
    {
        _client = new SftpClient(info);
        _exec = exec;
        HostKeyVerifier.Attach(_client);
    }

    public Task ConnectAsync() => Task.Run(_client.Connect);

    public Task<string> HomeAsync() => Task.Run(() => PosixPath.Normalize(_client.WorkingDirectory));

    public Task<IReadOnlyList<RemoteEntry>> ListAsync(string path) => Task.Run<IReadOnlyList<RemoteEntry>>(() =>
    {
        LoadNames();
        return _client.ListDirectory(path)
            .Where(f => f.Name is not ("." or ".."))
            .Select(f => new RemoteEntry(
                f.Name, f.FullName, f.IsDirectory, f.Attributes.IsSymbolicLink, f.Length, f.LastWriteTime,
                NameOf(_users, f.UserId), NameOf(_groups, f.GroupId), ModeOf(f.Attributes)))
            .ToList();
    });

    private static string NameOf(Dictionary<int, string>? names, int id) =>
        names is not null && names.TryGetValue(id, out var n) ? n : id.ToString();

    private static int ModeOf(SftpFileAttributes a) =>
        (a.OwnerCanRead ? 0x100 : 0) | (a.OwnerCanWrite ? 0x80 : 0) | (a.OwnerCanExecute ? 0x40 : 0)
        | (a.GroupCanRead ? 0x20 : 0) | (a.GroupCanWrite ? 0x10 : 0) | (a.GroupCanExecute ? 0x8 : 0)
        | (a.OthersCanRead ? 0x4 : 0) | (a.OthersCanWrite ? 0x2 : 0) | (a.OthersCanExecute ? 0x1 : 0);

    /// <summary>SFTP отдаёт числовые uid/gid; имена берём из getent. Если не вышло, показываем числа.</summary>
    private void LoadNames()
    {
        if (_users is not null) return;
        _users = new();
        _groups = new();
        if (_exec is not { IsConnected: true }) return;

        try
        {
            using var cmd = _exec.CreateCommand("getent passwd 2>/dev/null | cut -d: -f1,3; echo @@@; getent group 2>/dev/null | cut -d: -f1,3");
            var parts = cmd.Execute().Replace("\r", "").Split("@@@\n");
            Fill(_users, parts.ElementAtOrDefault(0));
            Fill(_groups, parts.ElementAtOrDefault(1));
        }
        catch
        {
            // имена не критичны
        }

        static void Fill(Dictionary<int, string> target, string? text)
        {
            foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Split(':');
                if (p.Length == 2 && int.TryParse(p[1], out var id)) target.TryAdd(id, p[0]);
            }
        }
    }

    public Task DownloadAsync(RemoteEntry entry, string localDir, IProgress<TransferProgress> progress) =>
        Task.Run(() => Download(entry.FullPath, entry.Name, entry.IsDirectory, localDir, progress));

    private void Download(string remotePath, string name, bool isDirectory, string localDir, IProgress<TransferProgress> progress)
    {
        var local = Path.Combine(localDir, name);
        if (isDirectory)
        {
            Directory.CreateDirectory(local);
            foreach (var f in _client.ListDirectory(remotePath).Where(f => f.Name is not ("." or "..")))
                Download(f.FullName, f.Name, f.IsDirectory, local, progress);
            return;
        }

        var total = _client.GetAttributes(remotePath).Size;
        using var stream = File.Create(local);
        _client.DownloadFile(remotePath, stream, done => progress.Report(new(name, (long)done, total)));
    }

    public Task UploadAsync(string localPath, string remoteDir, IProgress<TransferProgress> progress) =>
        Task.Run(() => Upload(localPath, remoteDir, progress));

    private void Upload(string localPath, string remoteDir, IProgress<TransferProgress> progress)
    {
        var name = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var remote = PosixPath.Combine(remoteDir, name);

        if (Directory.Exists(localPath))
        {
            if (!_client.Exists(remote)) _client.CreateDirectory(remote);
            foreach (var entry in Directory.EnumerateFileSystemEntries(localPath))
                Upload(entry, remote, progress);
            return;
        }

        var total = new FileInfo(localPath).Length;
        using var stream = File.OpenRead(localPath);
        _client.UploadFile(stream, remote, true, done => progress.Report(new(name, (long)done, total)));
    }

    public Task CreateDirectoryAsync(string path) => Task.Run(() => _client.CreateDirectory(path));

    public Task DeleteAsync(RemoteEntry entry) => Task.Run(() => Delete(entry.FullPath, entry.IsDirectory));

    private void Delete(string path, bool isDirectory)
    {
        if (!isDirectory)
        {
            _client.DeleteFile(path);
            return;
        }

        foreach (var f in _client.ListDirectory(path).Where(f => f.Name is not ("." or "..")))
            Delete(f.FullName, f.IsDirectory);
        _client.DeleteDirectory(path);
    }

    public Task RenameAsync(string oldPath, string newPath) => Task.Run(() => _client.RenameFile(oldPath, newPath));

    public Task ChangePermissionsAsync(string path, int mode) => Task.Run(() =>
    {
        // setuid, setgid и sticky через SFTP-вызов не задать, для них идём командой chmod по открытому SSH-соединению
        if ((mode & 0xE00) != 0)
        {
            if (_exec is not { IsConnected: true })
                throw new NotSupportedException(Loc.T("Особые права (setuid, setgid, sticky) можно менять только в режиме SCP"));
            using var cmd = _exec.CreateCommand($"chmod {PermissionBits.ToOctal(mode).PadLeft(4, '0')} -- {PosixPath.Quote(path)}");
            cmd.Execute();
            if (cmd.ExitStatus != 0) throw new IOException(cmd.Error.Trim());
            return;
        }

        // SSH.NET принимает права «цифрами»: 0644 (восьмеричное) передаётся как 644
        _client.ChangePermissions(path, short.Parse(PermissionBits.ToOctal(mode)));
    });

    public void Dispose() => _client.Dispose();
}
