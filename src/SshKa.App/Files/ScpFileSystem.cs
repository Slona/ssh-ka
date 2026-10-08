using SshKa.App.Localization;
using System.Globalization;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Common;
using SshKa.App.Storage;

namespace SshKa.App.Files;

/// <summary>
/// У SCP нет операции листинга, поэтому каталоги читаются командой ls по exec-каналу уже открытого SSH-соединения
/// (нужен GNU ls), а файлы передаются через scp.
/// </summary>
public sealed partial class ScpFileSystem : IRemoteFileSystem
{
    private readonly SshClient _exec;
    private readonly ScpClient _scp;

    public ScpFileSystem(SshClient exec)
    {
        _exec = exec;
        // имена файлов попадают в команду scp на сервере, поэтому пути обязательно экранируются
        _scp = new ScpClient(exec.ConnectionInfo, RemotePathTransformation.ShellQuote);
        HostKeyVerifier.Attach(_scp);
    }

    public Task ConnectAsync() => Task.Run(_scp.Connect);

    public async Task<string> HomeAsync() => PosixPath.Normalize((await RunAsync("cd && pwd")).Trim());

    // права, ссылки, владелец, группа, размер, дата ISO, имя
    [GeneratedRegex(@"^(?<perm>[\-dlcbsp][rwxsStT\-]{9})[.+@]?\s+\d+\s+(?<owner>\S+)\s+(?<group>\S+)\s+(?<size>\d+)\s+(?<date>\d{4}-\d\d-\d\d \d\d:\d\d)\s+(?<name>.+)$")]
    private static partial Regex LsLine();

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string path)
    {
        var output = await RunAsync($"LC_ALL=C ls -la --time-style=long-iso -- {PosixPath.Quote(path)}");
        var result = new List<RemoteEntry>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = LsLine().Match(line.TrimEnd('\r'));
            if (!m.Success) continue;

            var name = m.Groups["name"].Value;
            var isLink = m.Groups["perm"].Value[0] == 'l';
            if (isLink)
            {
                var arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow >= 0) name = name[..arrow];
            }
            if (name is "." or "..") continue;

            result.Add(new RemoteEntry(
                name,
                PosixPath.Combine(path, name),
                m.Groups["perm"].Value[0] == 'd',
                isLink,
                long.Parse(m.Groups["size"].Value, CultureInfo.InvariantCulture),
                DateTime.ParseExact(m.Groups["date"].Value, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                m.Groups["owner"].Value,
                m.Groups["group"].Value,
                PermissionBits.Parse(m.Groups["perm"].Value)));
        }
        return result;
    }

    public Task DownloadAsync(RemoteEntry entry, string localDir, IProgress<TransferProgress> progress) => Task.Run(() =>
    {
        void OnProgress(object? s, Renci.SshNet.Common.ScpDownloadEventArgs e) =>
            progress.Report(new(e.Filename, e.Downloaded, e.Size));

        _scp.Downloading += OnProgress;
        try
        {
            var local = Path.Combine(localDir, entry.Name);
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(local);
                _scp.Download(entry.FullPath, new DirectoryInfo(local));
            }
            else
            {
                _scp.Download(entry.FullPath, new FileInfo(local));
            }
        }
        finally { _scp.Downloading -= OnProgress; }
    });

    public async Task UploadAsync(string localPath, string remoteDir, IProgress<TransferProgress> progress)
    {
        // Upload(DirectoryInfo) кладёт в целевой каталог содержимое папки, а не саму папку — создаём её сами
        if (Directory.Exists(localPath))
        {
            var name = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var target = PosixPath.Combine(remoteDir, name);
            await RunAsync($"mkdir -p -- {PosixPath.Quote(target)}");
            await Task.Run(() => UploadCore(localPath, target, progress));
        }
        else
        {
            await Task.Run(() => UploadCore(localPath, remoteDir, progress));
        }
    }

    private void UploadCore(string localPath, string remoteDir, IProgress<TransferProgress> progress)
    {
        void OnProgress(object? s, Renci.SshNet.Common.ScpUploadEventArgs e) =>
            progress.Report(new(e.Filename, e.Uploaded, e.Size));

        _scp.Uploading += OnProgress;
        try
        {
            if (Directory.Exists(localPath))
                _scp.Upload(new DirectoryInfo(localPath), remoteDir);
            else
                _scp.Upload(new FileInfo(localPath), PosixPath.Combine(remoteDir, Path.GetFileName(localPath)));
        }
        finally { _scp.Uploading -= OnProgress; }
    }

    public Task CreateDirectoryAsync(string path) => RunAsync($"mkdir -- {PosixPath.Quote(path)}");

    public Task DeleteAsync(RemoteEntry entry) =>
        RunAsync(entry.IsDirectory
            ? $"rm -rf -- {PosixPath.Quote(entry.FullPath)}"
            : $"rm -f -- {PosixPath.Quote(entry.FullPath)}");

    public Task RenameAsync(string oldPath, string newPath) =>
        RunAsync($"mv -- {PosixPath.Quote(oldPath)} {PosixPath.Quote(newPath)}");

    public Task ChangePermissionsAsync(string path, int mode) =>
        RunAsync($"chmod {PermissionBits.ToOctal(mode)} -- {PosixPath.Quote(path)}");

    private Task<string> RunAsync(string command) => Task.Run(() =>
    {
        using var cmd = _exec.CreateCommand(command);
        var output = cmd.Execute();
        if (cmd.ExitStatus != 0)
            throw new IOException(string.IsNullOrWhiteSpace(cmd.Error) ? Loc.F("Команда завершилась с кодом {0}", cmd.ExitStatus) : cmd.Error.Trim());
        return output;
    });

    public void Dispose() => _scp.Dispose();
}
