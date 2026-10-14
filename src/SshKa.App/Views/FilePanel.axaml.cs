using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SshKa.App.Files;
using SshKa.App.Localization;
using SshKa.App.Storage;
using SshKa.App.Terminal;

namespace SshKa.App.Views;

/// <summary>Проводник одной SSH-сессии: просмотр каталогов сервера, загрузка и скачивание, права доступа.</summary>
public partial class FilePanel : UserControl
{
    private static readonly Dictionary<string, string> ColumnTitles = new()
    {
        ["name"] = "Имя", ["size"] = "Размер", ["date"] = "Изменён", ["owner"] = "Владелец", ["perm"] = "Права",
    };

    /// <summary>Ход загрузки или скачивания в процентах (null — передача закончилась). Показывается в нижней строке окна.</summary>
    public static event Action<double?>? TransferChanged;

    private readonly AppSettings _settings = AppSettings.Current;
    private readonly ObservableCollection<RemoteEntry> _items = new();
    private List<RemoteEntry> _current = new();
    private Func<TransferProtocol, Task<IRemoteFileSystem>>? _fsFactory;
    private IRemoteFileSystem? _fs;
    private string _path = "/";
    private bool _busy;
    private string _sortKey = "name";
    private bool _sortAscending = true;

    // пока строится форма, SelectionChanged/IsCheckedChanged срабатывают раньше, чем создаются остальные поля
    private bool _loading = true;

    public FilePanel()
    {
        InitializeComponent();
        Loc.Apply(this);
        Grid.ItemsSource = _items;
        ModeBox.SelectedIndex = (int)_settings.Transfer;
        FollowBox.IsChecked = _settings.FollowTerminal;
        Grid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);
        _loading = false;
        Resort(); // заголовки колонок на выбранном языке
    }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    /// <summary>Подключает панель к открытому SSH-соединению.</summary>
    public async void Attach(SshShellConnection conn, ShellCwdTracker tracker)
    {
        _fsFactory = async protocol =>
        {
            IRemoteFileSystem fs = protocol == TransferProtocol.Sftp
                ? new SftpFileSystem(conn.Client.ConnectionInfo, conn.Client)
                : new ScpFileSystem(conn.Client);
            try
            {
                await fs.ConnectAsync();
            }
            catch
            {
                fs.Dispose();
                throw;
            }
            return fs;
        };
        tracker.CwdChanged += cwd => Dispatcher.UIThread.Post(() => OnShellCwd(cwd));
        await NavigateAsync(null);
    }

    /// <summary>Подключает панель к произвольной файловой системе (для проверки внешнего вида без сервера).</summary>
    internal async Task AttachCustomAsync(Func<TransferProtocol, Task<IRemoteFileSystem>> factory, string? startPath = null)
    {
        _fsFactory = factory;
        await NavigateAsync(startPath);
    }

    public void Detach()
    {
        _fs?.Dispose();
        _fs = null;
    }

    private void OnShellCwd(string cwd)
    {
        if (FollowBox.IsChecked == true && cwd != _path && !_busy)
            _ = NavigateAsync(cwd);
    }

    // ---------- навигация ----------

    private async Task<IRemoteFileSystem> GetFsAsync()
    {
        if (_fs is not null) return _fs;
        _fs = await _fsFactory!(_settings.Transfer);
        return _fs;
    }

    /// <summary>Открывает каталог; null — домашний каталог.</summary>
    private async Task NavigateAsync(string? path)
    {
        if (_fsFactory is null) return;
        try
        {
            Status.Text = Loc.T("Загрузка…");
            var fs = await GetFsAsync();
            var target = path ?? await fs.HomeAsync();
            var entries = await fs.ListAsync(target);

            _current = entries.ToList();
            _path = target;
            PathBox.Text = target;
            Resort();
            Status.Text = Loc.F("{0} элементов", entries.Count);
        }
        catch (Exception ex)
        {
            Status.Text = Loc.F("Ошибка: {0}", ex.Message);
            PathBox.Text = _path;
        }
    }

    private async Task<string> HomeOrRootAsync()
    {
        try { return await (await GetFsAsync()).HomeAsync(); }
        catch { return "/"; }
    }

    private async void OnPathKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var text = (PathBox.Text ?? "").Trim();
        if (text.Length == 0) return;

        string path;
        if (text == "~" || text.StartsWith("~/"))
            path = PosixPath.Normalize(PosixPath.Combine(await HomeOrRootAsync(), text.TrimStart('~')));
        else if (text.StartsWith('/'))
            path = PosixPath.Normalize(text);
        else
            path = PosixPath.Normalize(PosixPath.Combine(_path, text));
        await NavigateAsync(path);
    }

    private async void OnUp(object? sender, RoutedEventArgs e) => await NavigateAsync(PosixPath.Parent(_path));

    private async void OnUpRowDoubleTapped(object? sender, TappedEventArgs e) => await NavigateAsync(PosixPath.Parent(_path));

    private async void OnRefresh(object? sender, RoutedEventArgs e) => await NavigateAsync(_path);

    private async void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        // двойной щелчок по заголовку или пустому месту не должен открывать выбранный каталог
        if (e.Source is not Visual v || !v.GetSelfAndVisualAncestors().OfType<DataGridRow>().Any()) return;
        if (Grid.SelectedItem is RemoteEntry { IsDirectory: true } or RemoteEntry { IsSymlink: true })
            await NavigateAsync(((RemoteEntry)Grid.SelectedItem).FullPath);
    }

    private async void OnModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || ModeBox.SelectedIndex < 0) return;
        _settings.Transfer = (TransferProtocol)ModeBox.SelectedIndex;
        _settings.Save();

        // следующее обращение создаст файловую систему нужного протокола
        _fs?.Dispose();
        _fs = null;
        await NavigateAsync(_path);
    }

    private void OnFollowChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.FollowTerminal = FollowBox.IsChecked == true;
        _settings.Save();
    }

    // ---------- сортировка ----------

    private void OnGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true; // сортируем сами: каталоги всегда сверху, строки меняются целиком
        if (e.Column.Tag is not string key) return;

        if (_sortKey == key) _sortAscending = !_sortAscending;
        else { _sortKey = key; _sortAscending = true; }
        Resort();
    }

    /// <summary>Каталоги всегда выше файлов; внутри групп порядок по выбранному столбцу.</summary>
    private void Resort()
    {
        var dirsFirst = _current.OrderByDescending(e => e.IsDirectory);
        IOrderedEnumerable<RemoteEntry> ordered = _sortKey switch
        {
            "size" => _sortAscending ? dirsFirst.ThenBy(e => e.Size) : dirsFirst.ThenByDescending(e => e.Size),
            "date" => _sortAscending ? dirsFirst.ThenBy(e => e.Modified) : dirsFirst.ThenByDescending(e => e.Modified),
            "owner" => _sortAscending
                ? dirsFirst.ThenBy(e => e.Owner, StringComparer.OrdinalIgnoreCase)
                : dirsFirst.ThenByDescending(e => e.Owner, StringComparer.OrdinalIgnoreCase),
            "perm" => _sortAscending ? dirsFirst.ThenBy(e => e.Mode) : dirsFirst.ThenByDescending(e => e.Mode),
            _ => _sortAscending
                ? dirsFirst.ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                : dirsFirst.ThenByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase),
        };

        _items.Clear();
        foreach (var e in ordered) _items.Add(e);

        foreach (var column in Grid.Columns)
        {
            if (column.Tag is not string key) continue;
            var arrow = key != _sortKey ? "" : _sortAscending ? " ▲" : " ▼";
            column.Header = Loc.T(ColumnTitles[key]) + arrow;
        }
    }

    // ---------- контекстное меню ----------

    /// <summary>Правая кнопка выбирает строку под курсором, чтобы меню относилось именно к ней.</summary>
    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Grid).Properties.IsRightButtonPressed) return;

        var row = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<DataGridRow>().FirstOrDefault();
        if (row?.DataContext is RemoteEntry entry)
        {
            if (Grid.SelectedItems is null || !Grid.SelectedItems.Contains(entry))
                Grid.SelectedItem = entry;
        }
        else
        {
            Grid.SelectedItems?.Clear(); // щелчок по пустому месту — меню без пунктов для файла
        }
    }

    private void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var count = SelectedEntries.Count;
        MenuDownload.IsEnabled = MenuDelete.IsEnabled = MenuPermissions.IsEnabled = count > 0;
        MenuRename.IsEnabled = count == 1;
    }

    // ---------- операции ----------

    private IReadOnlyList<RemoteEntry> SelectedEntries =>
        Grid.SelectedItems?.OfType<RemoteEntry>().ToList() ?? new List<RemoteEntry>();

    /// <summary>Выполняет операцию с индикацией хода; ошибки показывает в строке состояния.</summary>
    private async Task RunAsync(string title, Func<IRemoteFileSystem, IProgress<TransferProgress>, Task> action, bool refresh)
    {
        if (_fsFactory is null || _busy) return;
        _busy = true;
        TransferChanged?.Invoke(0);
        Status.Text = Loc.T(title) + "…";

        var progress = new Progress<TransferProgress>(p =>
        {
            TransferChanged?.Invoke(p.Total > 0 ? p.Done * 100.0 / p.Total : 0);
            Status.Text = $"{Loc.T(title)}: {p.Name}";
        });

        try
        {
            await action(await GetFsAsync(), progress);
            Status.Text = $"{Loc.T(title)}: {Loc.T("готово")}";
        }
        catch (Exception ex)
        {
            Status.Text = Loc.F("Ошибка: {0}", ex.Message);
        }
        finally
        {
            TransferChanged?.Invoke(null);
            _busy = false;
        }

        if (refresh) await NavigateAsync(_path);
    }

    /// <summary>
    /// Спрашивает, что делать с элементами, чьи имена уже заняты. Возвращает то, что нужно передавать,
    /// или null, если пользователь отменил операцию.
    /// </summary>
    private async Task<List<T>?> ResolveConflictsAsync<T>(
        IReadOnlyList<T> items, Func<T, string> nameOf, Func<T, bool> isFolder, Func<string, bool> exists)
    {
        var result = new List<T>();
        bool? forAll = null; // true — заменять всё, false — пропускать всё
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!exists(nameOf(item))) { result.Add(item); continue; }

            if (forAll is null)
            {
                if (Owner is not { } owner) return null;
                switch (await Dialogs.Conflict(owner, nameOf(item), isFolder(item), more: i < items.Count - 1))
                {
                    case Dialogs.ConflictChoice.Cancel: return null;
                    case Dialogs.ConflictChoice.ReplaceAll: forAll = true; break;
                    case Dialogs.ConflictChoice.SkipAll: forAll = false; break;
                    case Dialogs.ConflictChoice.Replace: result.Add(item); continue;
                    case Dialogs.ConflictChoice.Skip: continue;
                }
            }
            if (forAll == true) result.Add(item);
        }
        return result;
    }

    private bool NameTaken(string name) => _current.Any(e => e.Name == name);

    private async void OnDownload(object? sender, RoutedEventArgs e)
    {
        var selected = SelectedEntries;
        if (selected.Count == 0) { Status.Text = Loc.T("Выберите файлы или папки"); return; }

        var folders = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc.T("Куда сохранить"),
            AllowMultiple = false,
        });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } dir) return;

        _settings.LastDownloadDir = dir;
        _settings.Save();

        var toDownload = await ResolveConflictsAsync(selected, x => x.Name, x => x.IsDirectory,
            name => File.Exists(Path.Combine(dir, name)) || Directory.Exists(Path.Combine(dir, name)));
        if (toDownload is null || toDownload.Count == 0) { Status.Text = Loc.T("Скачивание отменено"); return; }

        await RunAsync("Скачивание", async (fs, progress) =>
        {
            foreach (var entry in toDownload) await fs.DownloadAsync(entry, dir, progress);
        }, refresh: false);
    }

    private async void OnUploadFiles(object? sender, RoutedEventArgs e)
    {
        var files = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("Файлы для загрузки"),
            AllowMultiple = true,
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        await UploadAsync(paths);
    }

    private async void OnUploadFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc.T("Папка для загрузки"),
            AllowMultiple = true,
        });
        var paths = folders.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        await UploadAsync(paths);
    }

    private async Task UploadAsync(IReadOnlyList<string> localPaths)
    {
        if (localPaths.Count == 0 || _fsFactory is null) return;
        var target = _path;

        // актуальный список каталога, а не показанный на экране: он мог устареть
        HashSet<string> existing;
        try
        {
            existing = (await (await GetFsAsync()).ListAsync(target)).Select(x => x.Name).ToHashSet();
        }
        catch (Exception ex)
        {
            Status.Text = Loc.F("Ошибка: {0}", ex.Message);
            return;
        }

        string NameOf(string p) => Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var toUpload = await ResolveConflictsAsync(localPaths, NameOf, Directory.Exists, existing.Contains);
        if (toUpload is null || toUpload.Count == 0) { Status.Text = Loc.T("Загрузка отменена"); return; }

        await RunAsync("Загрузка", async (fs, progress) =>
        {
            foreach (var p in toUpload) await fs.UploadAsync(p, target, progress);
        }, refresh: true);
    }

    private async void OnNewFolder(object? sender, RoutedEventArgs e)
    {
        if (Owner is not { } owner) return;
        var name = await Dialogs.AskText(owner, "Новая папка", "Название папки");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (NameTaken(name.Trim())) { Status.Text = Loc.F("«{0}» уже существует", name.Trim()); return; }
        var path = PosixPath.Combine(_path, name.Trim());
        await RunAsync("Создание папки", (fs, _) => fs.CreateDirectoryAsync(path), refresh: true);
    }

    private async void OnRename(object? sender, RoutedEventArgs e)
    {
        if (Owner is not { } owner || SelectedEntries.Count != 1) { Status.Text = Loc.T("Выберите один элемент"); return; }
        var entry = SelectedEntries[0];
        var name = await Dialogs.AskText(owner, "Переименовать", "Новое имя", entry.Name);
        if (string.IsNullOrWhiteSpace(name) || name == entry.Name) return;
        if (NameTaken(name.Trim())) { Status.Text = Loc.F("«{0}» уже существует", name.Trim()); return; }
        var target = PosixPath.Combine(_path, name.Trim());
        await RunAsync("Переименование", (fs, _) => fs.RenameAsync(entry.FullPath, target), refresh: true);
    }

    private async void OnPermissions(object? sender, RoutedEventArgs e)
    {
        var selected = SelectedEntries;
        if (Owner is not { } owner || selected.Count == 0) { Status.Text = Loc.T("Выберите файлы или папки"); return; }

        var first = selected[0];
        var heading = selected.Count == 1 ? Loc.F("Права для «{0}»", first.Name) : Loc.F("Права для выбранных элементов ({0})", selected.Count);
        var initial = first.Mode >= 0 ? first.Mode : 0x1A4; // 0644, если сервер права не сообщил
        var mode = await new PermissionsDialog(heading, initial, first.IsDirectory).ShowDialog<int?>(owner);
        if (mode is not { } newMode) return;

        await RunAsync("Изменение прав", async (fs, _) =>
        {
            foreach (var entry in selected) await fs.ChangePermissionsAsync(entry.FullPath, newMode);
        }, refresh: true);
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        var selected = SelectedEntries;
        if (Owner is not { } owner || selected.Count == 0) { Status.Text = Loc.T("Выберите файлы или папки"); return; }

        var what = selected.Count == 1 ? $"«{selected[0].Name}»" : Loc.F("выбранные элементы ({0})", selected.Count);
        if (!await Dialogs.Confirm(owner, "Удаление на сервере", Loc.F("Удалить {0}? Это действие нельзя отменить.", what), "Удалить")) return;

        await RunAsync("Удаление", async (fs, _) =>
        {
            foreach (var entry in selected) await fs.DeleteAsync(entry);
        }, refresh: true);
    }
}
