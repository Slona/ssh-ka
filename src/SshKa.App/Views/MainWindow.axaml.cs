using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Iciclecreek.Terminal;
using Porta.Pty;
using Renci.SshNet;
using SshKa.App.Highlight;
using SshKa.App.Import;
using SshKa.App.Localization;
using SshKa.App.Models;
using SshKa.App.Storage;
using SshKa.App.Terminal;

namespace SshKa.App.Views;

public partial class MainWindow : Window
{
    private static readonly DataFormat<string> SessionFormat = DataFormat.CreateStringApplicationFormat("sshka.session");

    private SessionVault? _vault;
    private readonly Control _noExplorer;

    // перетаскивание в дереве сессий
    private SessionItem? _pressedItem;
    private PointerPressedEventArgs? _pressedArgs;
    private Point _pressedAt;
    private SessionItem? _dragged;
    private SessionItem? _dropTarget;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closing += (_, _) => Save(); // заодно сохраняем, какие папки раскрыты

        HighlightEngine.Reload();
        Loc.Apply(this);
        MonitorMode.IsChecked = AppSettings.Current.ShowServerStatus;
        FilePanel.TransferChanged += value => Dispatcher.UIThread.Post(() =>
        {
            TransferBar.IsVisible = TransferName.IsVisible = TransferCancel.IsVisible = value is not null;
            if (value is { } state)
            {
                TransferBar.Value = state.Percent;
                TransferName.Text = state.Name;
            }
        });
        BuildImportMenu();
        BuildSplitMenu();
        BuildPanes(1);

        _noExplorer = new TextBlock
        {
            Text = Loc.T("Откройте SSH-сессию: здесь появятся файлы сервера."),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Opacity = 0.6,
            Margin = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // вопрос о ключе сервера задаётся из фонового потока подключения, диалог показываем в UI-потоке
        HostKeyVerifier.Confirm = request =>
            Dispatcher.UIThread.InvokeAsync(() => Dialogs.HostKey(this, request)).GetAwaiter().GetResult();

        SetupTreeDragAndDrop();
    }

    /// <summary>Окно с уже открытым хранилищем (без запроса мастер-пароля): для проверки внешнего вида.</summary>
    internal MainWindow(SessionVault vault) : this()
    {
        _vault = vault;
        Tree.ItemsSource = vault.Root;
    }

    // ---------- хранилище ----------

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_vault is not null) return;

        var path = SessionVault.DefaultPath;
        var create = !File.Exists(path);
        SessionVault? vault = null;

        // ключ, запомненный на этом компьютере, открывает хранилище без вопросов
        if (!create && !AppSettings.Current.AskMasterPassword && VaultKeyCache.TryLoad() is { } cached)
        {
            try { vault = SessionVault.UnlockWithKey(path, cached); }
            catch (Exception) { VaultKeyCache.Delete(); } // ключ устарел (например, сменили пароль на другом компьютере)
        }
        if (vault is not null)
        {
            _vault = vault;
            Tree.ItemsSource = vault.Root;
            return;
        }

        var ok = await Dialogs.Unlock(this, create, pw => Task.Run<string?>(() =>
        {
            try
            {
                vault = create ? SessionVault.Create(path, pw) : SessionVault.Unlock(path, pw);
                return null;
            }
            catch (CryptographicException) { return Loc.T("Неверный мастер-пароль"); }
            catch (Exception ex) { return ex.Message; }
        }));

        if (!ok || vault is null)
        {
            Close();
            return;
        }

        _vault = vault;
        Tree.ItemsSource = vault.Root;
        // после первого ввода пароля запоминаем ключ, чтобы дальше не спрашивать
        if (!AppSettings.Current.AskMasterPassword) VaultKeyCache.Save(vault.ExportKey());
    }

    private void Save()
    {
        RefreshSearch();
        try { _vault?.Save(); }
        catch (Exception ex) { StatusText.Text = Loc.F("Не удалось сохранить: {0}", ex.Message); }
    }

    // ---------- левая панель: сессии или проводник ----------

    private ColumnDefinition LeftColumn => BodyGrid.ColumnDefinitions[0];
    private bool _collapsed;
    private double _leftWidth = 400;

    private void OnSessionsMode(object? sender, RoutedEventArgs e) => SetLeftMode(explorer: false);

    private void OnExplorerMode(object? sender, RoutedEventArgs e) => SetLeftMode(explorer: true);

    private void SetLeftMode(bool explorer, bool expand = true)
    {
        if (_collapsed && expand) SetCollapsed(false); // выбрали раздел — панель разворачивается
        SessionsMode.IsChecked = !explorer;
        ExplorerMode.IsChecked = explorer;
        ExplorerHost.IsVisible = explorer;
        UpdateSessionsView();
        RefreshExplorer();
    }

    private void OnCancelTransfer(object? sender, RoutedEventArgs e) => FilePanel.CancelTransfers();

    private void OnCollapse(object? sender, RoutedEventArgs e) => SetCollapsed(!_collapsed);

    /// <summary>Сворачивает панель в узкую полоску с кнопками (чтобы освободить место терминалу) и возвращает обратно.</summary>
    private void SetCollapsed(bool collapsed)
    {
        if (_collapsed == collapsed) return;
        if (collapsed) _leftWidth = LeftColumn.ActualWidth > 0 ? LeftColumn.ActualWidth : 400;
        _collapsed = collapsed;

        var orientation = collapsed ? Orientation.Vertical : Orientation.Horizontal;
        BarLeft.Orientation = BarRight.Orientation = orientation;
        BarGrid.ColumnDefinitions = new ColumnDefinitions(collapsed ? "*" : "*,Auto");
        BarGrid.RowDefinitions = new RowDefinitions(collapsed ? "Auto,Auto" : "Auto");
        Grid.SetColumn(BarRight, collapsed ? 0 : 1);
        Grid.SetRow(BarRight, collapsed ? 1 : 0);
        BarRight.HorizontalAlignment = collapsed ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        DockPanel.SetDock(BarBorder, collapsed ? Dock.Top : Dock.Bottom);
        BarBorder.BorderThickness = collapsed ? new Thickness(0) : new Thickness(0, 1, 0, 0);
        LeftContent.IsVisible = !collapsed;
        LeftSplitter.IsEnabled = !collapsed;
        CollapseIcon.RenderTransform = collapsed ? new ScaleTransform(-1, 1) : null;
        CollapseIcon.RenderTransformOrigin = RelativePoint.Center;

        if (collapsed)
        {
            LeftColumn.MinWidth = 0;
            LeftColumn.MaxWidth = 46;
            LeftColumn.Width = new GridLength(46);
        }
        else
        {
            LeftColumn.MinWidth = 220;
            LeftColumn.MaxWidth = 700;
            LeftColumn.Width = new GridLength(Math.Clamp(_leftWidth, 220, 700));
        }
    }

    /// <summary>Включает и выключает панель состояния сервера во всех открытых SSH-вкладках.</summary>
    private void OnMonitorToggled(object? sender, RoutedEventArgs e)
    {
        var on = MonitorMode.IsChecked == true;
        AppSettings.Current.ShowServerStatus = on;
        AppSettings.Current.Save();
        foreach (var view in Tabs.Items.OfType<TabItem>().Select(t => t.Tag).OfType<SessionView>())
            view.SetMonitoring(on);
    }

    private void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Tabs.SelectedItem is TabItem tab)
        {
            ShowInPane(tab);
            FocusTerminal(tab);
        }
        RefreshExplorer();
    }

    /// <summary>Передаёт клавиатурный фокус терминалу вкладки (иначе клавиши уходят в дерево сессий).</summary>
    private void FocusTerminal(TabItem tab)
    {
        if (TerminalOf(tab.Tag) is { } terminal)
            Dispatcher.UIThread.Post(() => terminal.Focus(), DispatcherPriority.Input);
    }

    /// <summary>В режиме проводника показывает файлы активной вкладки (если это SSH-сессия).</summary>
    private void RefreshExplorer()
    {
        if (ExplorerMode.IsChecked != true) return;
        ExplorerHost.Content = (Tabs.SelectedItem as TabItem)?.Tag is SessionView view ? view.Files : _noExplorer;
    }

    // ---------- верхняя панель ----------

    private async void OnSettings(object? sender, RoutedEventArgs e)
    {
        var language = AppSettings.Current.Language;
        if (!await new SettingsWindow(_vault).ShowDialog<bool>(this)) return;

        // новые значения подхватываем у уже открытых терминалов
        foreach (var terminal in Tabs.Items.OfType<TabItem>().Select(t => t.Tag).Select(TerminalOf).OfType<TerminalControl>())
            TerminalFactory.ApplySettings(terminal);
        if (language != AppSettings.Current.Language)
            await Dialogs.Info(this, "Язык интерфейса", "Язык сменится после перезапуска программы.");
    }

    private async void OnAbout(object? sender, RoutedEventArgs e) => await new AboutWindow().ShowDialog(this);

    private async void OnExit(object? sender, RoutedEventArgs e)
    {
        var open = Tabs.Items.Count;
        var text = open > 0
            ? Loc.F("Закрыть программу? Открытые вкладки ({0}) и их соединения будут закрыты.", open)
            : Loc.T("Закрыть программу?");
        if (!await Dialogs.Confirm(this, "Выход", text, "Выйти")) return;
        Close();
    }

    // ---------- импорт ----------

    private static MenuItem MenuEntry(string text, EventHandler<RoutedEventArgs> click)
    {
        var item = new MenuItem { Header = Loc.T(text) };
        item.Click += click;
        return item;
    }

    private void BuildImportMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(MenuEntry("Из MobaXterm (.mxtsessions, .ini)…", OnImportMoba));
        menu.Items.Add(MenuEntry("Из PuTTY (реестр Windows)", OnImportPutty));
        menu.Items.Add(MenuEntry("Из ~/.ssh/config", OnImportSshConfig));
        ImportButton.Flyout = menu;
        ToolTip.SetTip(ImportButton, Loc.T("Импорт сессий"));
    }

    private async void OnImportMoba(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("Файл сессий MobaXterm"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(Loc.T("Сессии MobaXterm")) { Patterns = new[] { "*.mxtsessions", "*.ini" } },
                FilePickerFileTypes.All,
            },
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;

        try { await AddImportedAsync("MobaXterm", MobaXtermImporter.ParseFile(path)); }
        catch (Exception ex) { await Dialogs.Info(this, "Импорт", Loc.F("Не удалось прочитать файл: {0}", ex.Message)); }
    }

    private async void OnImportPutty(object? sender, RoutedEventArgs e)
    {
        if (!PuttyImporter.IsAvailable)
        {
            await Dialogs.Info(this, "Импорт", "Импорт из PuTTY работает только в Windows.");
            return;
        }
        try { await AddImportedAsync("PuTTY", PuttyImporter.ReadRegistry()); }
        catch (Exception ex) { await Dialogs.Info(this, "Импорт", Loc.F("Не удалось прочитать реестр: {0}", ex.Message)); }
    }

    private async void OnImportSshConfig(object? sender, RoutedEventArgs e)
    {
        var path = SshConfigImporter.DefaultPath;
        if (!File.Exists(path))
        {
            await Dialogs.Info(this, "Импорт", Loc.F("Файл не найден: {0}", path));
            return;
        }
        try { await AddImportedAsync("ssh config", SshConfigImporter.ParseFile(path)); }
        catch (Exception ex) { await Dialogs.Info(this, "Импорт", Loc.F("Не удалось прочитать файл: {0}", ex.Message)); }
    }

    /// <summary>Кладёт импортированное в отдельную папку (повторный импорт не смешивается с прежним) и сообщает итог.</summary>
    private async Task AddImportedAsync(string source, ImportResult result)
    {
        if (_vault is null) return;
        if (result.SessionCount == 0)
        {
            await Dialogs.Info(this, "Импорт", Loc.F("{0}: сессий для импорта не найдено.", source) + SkippedText(result));
            return;
        }

        var folder = new SessionItem { IsFolder = true, Name = Loc.F("Импорт {0} {1:yyyy-MM-dd HH:mm}", source, DateTime.Now) };
        foreach (var item in result.Items) folder.Children.Add(item);
        _vault.Root.Add(folder);
        Save();

        await Dialogs.Info(this, "Импорт завершён",
            Loc.F("{0}: импортировано сессий — {1}. Они лежат в папке «{2}».", source, result.SessionCount, folder.Name) + "\n\n" +
            Loc.T("Пароли не импортируются: у MobaXterm и PuTTY они хранятся отдельно и в защищённом виде. " +
                  "Пароль будет запрошен при подключении, либо введите его в свойствах сессии.") + SkippedText(result));
    }

    private static string SkippedText(ImportResult r) => r.Skipped.Count == 0
        ? ""
        : "\n\n" + Loc.F("Пропущено ({0}, неподдерживаемые типы): ", r.Skipped.Count) + string.Join(", ", r.Skipped.Take(15))
          + (r.Skipped.Count > 15 ? "…" : "");

    // ---------- дерево сессий ----------

    // выбранная сессия: в списке результатов, пока идёт поиск, иначе в дереве
    private SessionItem? Selected => IsSearching ? (SearchResults.SelectedItem as SearchHit)?.Item : Tree.SelectedItem as SessionItem;

    private bool IsSearching => !string.IsNullOrWhiteSpace(SearchBox.Text);

    private void UpdateSessionsView()
    {
        var sessions = SessionsMode.IsChecked == true;
        var searching = IsSearching;
        SearchBox.IsVisible = sessions;
        Tree.IsVisible = sessions && !searching;
        SearchResults.IsVisible = sessions && searching;
        NoResults.IsVisible = sessions && searching && SearchResults.ItemCount == 0;
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => RefreshSearch();

    /// <summary>Пересчитывает результаты по тексту поиска: слова через пробел, все должны встретиться в имени, адресе, логине или пути.</summary>
    private void RefreshSearch()
    {
        if (_vault is not null && IsSearching)
        {
            var words = SearchBox.Text!.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var hits = new List<SearchHit>();
            void Walk(IEnumerable<SessionItem> level, string path)
            {
                foreach (var item in level)
                {
                    if (item.IsFolder)
                    {
                        Walk(item.Children, path.Length == 0 ? item.Name : path + " / " + item.Name);
                        continue;
                    }
                    var haystack = string.Join(' ', item.Name, item.Host, item.Username, path);
                    if (words.All(w => haystack.Contains(w, StringComparison.CurrentCultureIgnoreCase)))
                        hits.Add(new SearchHit(item, path));
                }
            }
            Walk(_vault.Root, "");
            SearchResults.ItemsSource = hits;
            if (hits.Count > 0) SearchResults.SelectedIndex = 0;
        }
        else
        {
            SearchResults.ItemsSource = null;
        }
        UpdateSessionsView();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                SearchBox.Text = "";
                e.Handled = true;
                break;
            case Key.Enter when Selected is { IsFolder: false } item:
                Connect(item);
                e.Handled = true;
                break;
            case Key.Down or Key.Up when SearchResults.ItemCount > 0:
                // стрелки двигают выбор в результатах, не уводя курсор из поля
                var step = e.Key == Key.Down ? 1 : -1;
                SearchResults.SelectedIndex = Math.Clamp(SearchResults.SelectedIndex + step, 0, SearchResults.ItemCount - 1);
                e.Handled = true;
                break;
        }
    }

    private void OnSearchDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Selected is { IsFolder: false } item) Connect(item);
    }

    /// <summary>Куда добавлять новый узел: в выбранную папку, рядом с выбранной сессией или в корень.</summary>
    private ObservableCollection<SessionItem> TargetCollection()
    {
        if (Selected is { IsFolder: true } folder) return folder.Children;
        if (Selected is { } s && _vault!.FindContainer(s) is { } c) return c;
        return _vault!.Root;
    }

    /// <summary>Пункт меню «Новая сессия»: только создаёт сессию в дереве.</summary>
    private async void OnNewSession(object? sender, RoutedEventArgs e) => await CreateSessionAsync(connect: false);

    /// <summary>Кнопка «Сессия» сверху: создаёт сессию и сразу открывает вкладку с подключением.</summary>
    private async void OnNewSessionAndConnect(object? sender, RoutedEventArgs e) => await CreateSessionAsync(connect: true);

    private async Task CreateSessionAsync(bool connect)
    {
        if (_vault is null) return;
        var item = new SessionItem();
        if (!await new SessionEditDialog(item).ShowDialog<bool>(this)) return;
        if (IsSearching) SearchBox.Text = ""; // новая сессия должна быть видна в дереве
        if (Selected is { IsFolder: true } folder) folder.IsExpanded = true;
        TargetCollection().Add(item);
        Save();
        if (connect) Connect(item);
    }

    private async void OnNewFolder(object? sender, RoutedEventArgs e)
    {
        if (_vault is null) return;
        var name = await Dialogs.AskText(this, "Новая папка", "Название папки");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (Selected is { IsFolder: true } folder) folder.IsExpanded = true;
        TargetCollection().Add(new SessionItem { IsFolder = true, Name = name.Trim() });
        Save();
    }

    private async void OnEdit(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        if (item.IsFolder)
        {
            var name = await Dialogs.AskText(this, "Переименовать папку", "Название папки", item.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            item.Name = name.Trim();
            Save();
        }
        else if (await new SessionEditDialog(item).ShowDialog<bool>(this))
        {
            // логин показывается в подписи в скобках
            item.Username = item.Username;
            Save();
        }
    }

    private void OnDuplicate(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { IsFolder: false } item || _vault?.FindContainer(item) is not { } container) return;
        container.Insert(container.IndexOf(item) + 1, new SessionItem
        {
            Name = item.Name + Loc.T(" (копия)"),
            Protocol = item.Protocol,
            Host = item.Host,
            Port = item.Port,
            Username = item.Username,
            Auth = item.Auth,
            KeyPath = item.KeyPath,
            Password = item.Password,
            Passphrase = item.Passphrase,
        });
        Save();
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } item || _vault?.FindContainer(item) is not { } container) return;
        var what = item.IsFolder && item.Children.Count > 0
            ? Loc.F("Удалить папку «{0}» вместе со всем содержимым?", item.Name)
            : Loc.F("Удалить «{0}»?", item.Name);
        if (!await Dialogs.Confirm(this, "Удаление", what, "Удалить")) return;
        container.Remove(item);
        Save();
    }

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Selected is { IsFolder: false } item) Connect(item);
    }

    private void OnConnectSelected(object? sender, RoutedEventArgs e)
    {
        if (Selected is { IsFolder: false } item) Connect(item);
    }

    // ---------- перетаскивание сессий и папок ----------

    private void SetupTreeDragAndDrop()
    {
        Tree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
        Tree.AddHandler(PointerMovedEvent, OnTreePointerMoved, RoutingStrategies.Tunnel);
        Tree.AddHandler(PointerReleasedEvent, (_, _) => { _pressedItem = null; _pressedArgs = null; }, RoutingStrategies.Tunnel);

        DragDrop.SetAllowDrop(Tree, true);
        // на новой строке событие начинается с DragEnter, дальше идут DragOver: обрабатываем оба
        DragDrop.AddDragEnterHandler(Tree, OnTreeDragOver);
        DragDrop.AddDragOverHandler(Tree, OnTreeDragOver);
        DragDrop.AddDragLeaveHandler(Tree, (_, _) => ClearDropIndicator());
        DragDrop.AddDropHandler(Tree, OnTreeDrop);
    }

    private static Border? RowOf(Visual? visual) =>
        visual?.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Name == "RowBorder");

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressedItem = null;
        if (!e.GetCurrentPoint(Tree).Properties.IsLeftButtonPressed) return;

        // запоминаем строку и нажатие; само перетаскивание начнётся, только если мышь сместится
        _pressedItem = RowOf(e.Source as Visual)?.DataContext as SessionItem;
        _pressedArgs = e;
        _pressedAt = e.GetPosition(Tree);
    }

    private async void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedItem is null || _pressedArgs is null || _dragged is not null) return;
        if (!e.GetCurrentPoint(Tree).Properties.IsLeftButtonPressed) return;

        var d = e.GetPosition(Tree) - _pressedAt;
        if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6) return;

        var item = _pressedItem;
        var args = _pressedArgs;
        _pressedItem = null;
        _pressedArgs = null;
        _dragged = item;

        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(SessionFormat, item.Id.ToString()));
            await DragDrop.DoDragDropAsync(args, data, DragDropEffects.Move);
        }
        finally
        {
            ClearDropIndicator();
            _dragged = null;
        }
    }

    /// <summary>Строка под курсором и положение относительно неё: выше, ниже или внутрь папки.</summary>
    private (SessionItem? Target, DropIndicator Position) LocateDrop(DragEventArgs e)
    {
        var point = e.GetPosition(Tree);
        var row = RowOf(Tree.InputHitTest(point) as Visual);
        if (row?.DataContext is not SessionItem target) return (null, DropIndicator.None);

        var top = row.TranslatePoint(new Point(0, 0), Tree)?.Y ?? 0;
        var y = point.Y - top;
        var h = Math.Max(1, row.Bounds.Height);

        if (target.IsFolder)
            return (target, y < h * 0.25 ? DropIndicator.Before : y > h * 0.75 ? DropIndicator.After : DropIndicator.Into);
        return (target, y < h * 0.5 ? DropIndicator.Before : DropIndicator.After);
    }

    /// <summary>Нельзя бросить узел на самого себя или внутрь собственного содержимого.</summary>
    private static bool CanDrop(SessionItem dragged, SessionItem? target) =>
        target is null || (target != dragged && !dragged.Contains(target));

    private void OnTreeDragOver(object? sender, DragEventArgs e)
    {
        if (_dragged is null) { e.DragEffects = DragDropEffects.None; return; }

        var (target, position) = LocateDrop(e);
        var valid = CanDrop(_dragged, target);
        e.DragEffects = valid ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;

        ShowDropIndicator(valid ? target : null, position);
    }

    private void ShowDropIndicator(SessionItem? target, DropIndicator position)
    {
        if (!ReferenceEquals(_dropTarget, target) && _dropTarget is not null)
            _dropTarget.DropIndicator = DropIndicator.None;
        _dropTarget = target;
        if (target is not null) target.DropIndicator = position;
    }

    private void ClearDropIndicator() => ShowDropIndicator(null, DropIndicator.None);

    private void OnTreeDrop(object? sender, DragEventArgs e)
    {
        var dragged = _dragged;
        if (dragged is null || _vault is null) return;

        var (target, position) = LocateDrop(e);
        ClearDropIndicator();
        e.Handled = true;
        if (!CanDrop(dragged, target)) return;

        MoveItem(dragged, target, position);
    }

    /// <summary>Переносит узел: внутрь папки, выше или ниже указанного. Без цели — в конец корня.</summary>
    private void MoveItem(SessionItem item, SessionItem? target, DropIndicator position)
    {
        if (_vault is null || _vault.FindContainer(item) is not { } from) return;

        if (target is null)
        {
            from.Remove(item);
            _vault.Root.Add(item);
        }
        else if (position == DropIndicator.Into)
        {
            from.Remove(item);
            target.Children.Add(item);
            target.IsExpanded = true;
        }
        else
        {
            if (_vault.FindContainer(target) is not { } to) return;
            from.Remove(item);
            var index = to.IndexOf(target); // после удаления: индекс цели мог сдвинуться
            to.Insert(position == DropIndicator.After ? index + 1 : index, item);
        }

        Save();
        Tree.SelectedItem = item;
    }

    // ---------- вкладки и области ----------

    // Полоса вкладок (Tabs) только переключает и подписывает; сами терминалы лежат в областях справа.
    // Областей одна, две (рядом) или четыре (2×2): в каждой своя вкладка, нажатая на полосе вкладка попадает в активную область.
    private sealed class Pane
    {
        public required Border Frame { get; init; }
        public required ContentControl Host { get; init; }
        public TabItem? Tab { get; set; }
    }

    /// <summary>Что известно о вкладке: как её закрыть, как перезапустить и остановлена ли сессия.</summary>
    private sealed class TabState
    {
        public required Action OnClose { get; set; }
        /// <summary>Что сделать, когда сеанс закончился (задаётся при каждом подключении).</summary>
        public Action? OnEnded { get; set; }
        public Action? Restart { get; set; }
        public Action? Duplicate { get; set; }
        public SessionItem? Session { get; set; }
        public bool StoppedCounted { get; set; }
        public TerminalControl? Terminal { get; set; }
        public bool Stopped { get; set; }
    }

    private readonly Dictionary<TabItem, TabState> _states = new();
    private readonly List<Pane> _panes = new();

    // Терминал, снятый с экрана (вкладка ушла из области), должен знать, что его только переносят: иначе после возвращения он
    // перестаёт принимать ввод и показывать вывод. Контрол для этого имеет пару BeginReparent / EndReparent.
    private readonly HashSet<TerminalControl> _reparenting = new();

    private void DetachContent(ContentControl host)
    {
        if (TerminalOf(host.Content) is { } terminal && _reparenting.Add(terminal))
        {
            try { terminal.BeginReparent(); } catch { /* контрол ещё не показывался */ }
        }
        host.Content = null;
    }

    private void AttachContent(ContentControl host, Control? view)
    {
        host.Content = view;
        if (TerminalOf(view) is { } terminal && _reparenting.Remove(terminal))
        {
            try { terminal.EndReparent(); } catch { /* контрол ещё не показывался */ }
        }
    }
    private int _focus;

    private static Control? ViewOf(TabItem? tab) => tab?.Tag as Control;

    /// <summary>Терминал внутри содержимого вкладки: сам контрол, рамка вокруг него или SSH-вид.</summary>
    private static TerminalControl? TerminalOf(object? view) => view switch
    {
        SessionView s => s.Terminal,
        Border { Child: Grid g } when g.Children.OfType<TerminalControl>().FirstOrDefault() is { } t => t,
        TerminalControl t => t,
        _ => null,
    };

    private void BuildSplitMenu()
    {
        var menu = new MenuFlyout();
        foreach (var (count, text, icon) in new[]
                 {
                     (1, "Одна область", "IconSingle"),
                     (2, "Две области", "IconTwoColumns"),
                     (4, "Четыре области", "IconFourPanes"),
                 })
        {
            var item = MenuEntry(text, (_, _) => BuildPanes(count));
            item.Icon = new PathIcon { Data = IconResources.Get(icon) };
            menu.Items.Add(item);
        }
        SplitButton.Flyout = menu;
        ToolTip.SetTip(SplitButton, Loc.T("Показать несколько сессий в окне"));
    }

    /// <summary>Перестраивает сетку областей; уже показанные вкладки остаются на месте, лишние освобождаются.</summary>
    private void BuildPanes(int count)
    {
        // освобождаем содержимое, чтобы его можно было положить в новые рамки
        foreach (var pane in _panes) DetachContent(pane.Host);
        var previous = _panes.Select(p => p.Tab).ToList();
        _panes.Clear();
        PaneGrid.Children.Clear();

        var columns = count == 1 ? 1 : 2;
        var rows = count == 4 ? 2 : 1;
        PaneGrid.ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", columns)));
        PaneGrid.RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("*", rows)));

        for (var i = 0; i < count; i++)
        {
            var host = new ContentControl
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
            };
            var frame = new Border { Classes = { "pane" }, Child = host };
            var index = i;
            frame.AddHandler(PointerPressedEvent, (_, _) => FocusPane(index), RoutingStrategies.Tunnel);
            Grid.SetColumn(frame, i % columns);
            Grid.SetRow(frame, i / columns);
            PaneGrid.Children.Add(frame);
            _panes.Add(new Pane { Frame = frame, Host = host });
        }

        _focus = Math.Min(_focus, count - 1);
        for (var i = 0; i < count && i < previous.Count; i++) Assign(_panes[i], previous[i]);
        FillEmptyPanes();
        UpdateFrames();
    }

    private void FocusPane(int index)
    {
        if (index >= _panes.Count) return;
        _focus = index;
        if (_panes[index].Tab is { } tab && !ReferenceEquals(Tabs.SelectedItem, tab)) Tabs.SelectedItem = tab;
        UpdateFrames();
    }

    private void UpdateFrames()
    {
        for (var i = 0; i < _panes.Count; i++)
            _panes[i].Frame.Classes.Set("focused", _panes.Count > 1 && i == _focus);
    }

    /// <summary>Кладёт вкладку в область (null — пустая область с подсказкой).</summary>
    private void Assign(Pane pane, TabItem? tab)
    {
        if (ReferenceEquals(pane.Tab, tab) && pane.Host.Content is not null) return;
        DetachContent(pane.Host);
        pane.Tab = tab;
        if (tab is null)
        {
            pane.Host.Content = new TextBlock
            {
                Text = Loc.T("Выберите вкладку на панели вкладок: она появится в этой области."),
                Opacity = 0.5,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(24),
            };
            return;
        }
        AttachContent(pane.Host, ViewOf(tab));
    }

    /// <summary>Выбранная на полосе вкладка: если она уже видна — переходим в её область, иначе кладём в активную.</summary>
    private void ShowInPane(TabItem tab)
    {
        var shown = _panes.FindIndex(p => ReferenceEquals(p.Tab, tab));
        if (shown >= 0)
            _focus = shown;
        else if (_panes.Count > 0)
            Assign(_panes[_focus], tab);
        UpdateFrames();
    }

    /// <summary>Пустые области заполняем вкладками, которые сейчас нигде не показаны.</summary>
    private void FillEmptyPanes()
    {
        var hidden = Tabs.Items.OfType<TabItem>().Where(t => _panes.All(p => !ReferenceEquals(p.Tab, t))).ToList();
        foreach (var pane in _panes.Where(p => p.Tab is null))
        {
            if (hidden.Count == 0)
            {
                DetachContent(pane.Host); // чтобы Assign заново показал подсказку
                Assign(pane, null);
                continue;
            }
            Assign(pane, hidden[0]);
            hidden.RemoveAt(0);
        }
    }

    private void RemoveTab(TabItem tab)
    {
        foreach (var pane in _panes.Where(p => ReferenceEquals(p.Tab, tab)))
        {
            DetachContent(pane.Host);
            pane.Tab = null;
        }
        Tabs.Items.Remove(tab);
        if (_states.TryGetValue(tab, out var closing) && closing is { StoppedCounted: true, Session: { } owner })
            owner.StoppedCount--; // вкладка с завершённым сеансом закрыта — красная подсветка уходит
        _states.Remove(tab);
        tab.Tag = null;
        FillEmptyPanes();
        UpdateFrames();
        // закрыли последнюю вкладку: проводнику показывать нечего, возвращаем список сессий
        if (Tabs.Items.Count == 0 && ExplorerMode.IsChecked == true) SetLeftMode(explorer: false, expand: false);
        RefreshExplorer();
    }

    /// <summary>Добавляет вкладку с иконкой и кнопкой закрытия; onClose освобождает соединение.</summary>
    internal TabItem AddTab(string title, Control content, Action onClose, int? position = null)
    {
        var tab = new TabItem { Tag = content };
        _states[tab] = new TabState { OnClose = onClose, Terminal = TerminalOf(content) };
        tab.ContextMenu = BuildTabMenu(tab);

        var icon = new PathIcon { Width = 12, Height = 12, Data = IconResources.Get("IconTerminal"), Foreground = Brushes.DimGray };
        var close = new Button
        {
            Classes = { "close" },
            Content = new PathIcon { Data = IconResources.Get("IconClose") },
        };
        close.Click += (_, _) =>
        {
            onClose();
            RemoveTab(tab);
        };

        tab.Header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { icon, new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center }, close },
        };

        if (position is { } at && at >= 0 && at <= Tabs.Items.Count) Tabs.Items.Insert(at, tab);
        else Tabs.Items.Add(tab);
        Tabs.SelectedItem = tab;
        return tab;
    }

    /// <summary>Меню по правой кнопке на названии вкладки: копировать, закрыть, сохранить вывод.</summary>
    private ContextMenu BuildTabMenu(TabItem tab)
    {
        var copy = new MenuItem { Header = Loc.T("Копировать вкладку"), Icon = new PathIcon { Data = IconResources.Get("IconPlus"), Width = 12, Height = 12 } };
        var close = new MenuItem { Header = Loc.T("Закрыть вкладку"), Icon = new PathIcon { Data = IconResources.Get("IconClose"), Width = 10, Height = 10 } };
        var save = new MenuItem { Header = Loc.T("Сохранить вывод терминала"), Icon = new PathIcon { Data = IconResources.Get("IconDownload"), Width = 12, Height = 12 } };

        copy.Click += (_, _) =>
        {
            if (_states.TryGetValue(tab, out var state)) state.Duplicate?.Invoke();
        };
        close.Click += (_, _) =>
        {
            if (!_states.TryGetValue(tab, out var state)) return;
            state.OnClose();
            RemoveTab(tab);
        };
        save.Click += (_, _) =>
        {
            if (_states.TryGetValue(tab, out var state) && state.Terminal is { } terminal) _ = SaveOutputAsync(terminal, tab);
        };

        var menu = new ContextMenu { Items = { copy, close, new Separator(), save } };
        menu.Opening += (_, _) =>
        {
            _states.TryGetValue(tab, out var state);
            copy.IsEnabled = state?.Duplicate is not null;
            save.IsEnabled = state?.Terminal is not null;
        };
        return menu;
    }

    // ---------- сессия завершилась: сообщение в окне, Enter / R / S ----------

    private static string StoppedMessage()
    {
        const string esc = "\u001b";
        return "\r\n" + esc + "[0m" + esc + "[91m" + Loc.T("Сеанс завершён") + esc + "[0m\r\n"
               + "    - " + Loc.T("Нажмите <Enter>, чтобы закрыть вкладку") + "\r\n"
               + "    - " + Loc.T("Нажмите R, чтобы перезапустить сеанс") + "\r\n"
               + "    - " + Loc.T("Нажмите S, чтобы сохранить вывод терминала в файл") + "\r\n";
    }

    /// <summary>Подключает обработку клавиш «остановленной» вкладки. Пока сессия жива, клавиши идут терминалу как обычно.</summary>
    private void WireStopKeys(TabItem tab, TerminalControl terminal)
    {
        // собственное сообщение терминала о выходе процесса нам не нужно
        terminal.WritingMessage += (_, e) =>
        {
            if (e.Kind == TerminalMessageKind.ProcessExited) e.Handled = true;
        };

        terminal.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (!_states.TryGetValue(tab, out var state) || !state.Stopped) return;
            e.Handled = true;
            switch (e.Key)
            {
                case Key.Enter:
                    state.OnClose();
                    RemoveTab(tab);
                    break;
                case Key.R:
                    state.Restart?.Invoke();
                    break;
                case Key.S:
                    _ = SaveOutputAsync(terminal, tab);
                    break;
            }
        }, RoutingStrategies.Tunnel);

        // символы набранных клавиш в мёртвое соединение не отправляем
        terminal.AddHandler(InputElement.TextInputEvent, (_, e) =>
        {
            if (_states.TryGetValue(tab, out var state) && state.Stopped) e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>Сессия закончилась (выход, обрыв, закрытие сервером): пишем сообщение в самом терминале.</summary>
    private void MarkStopped(TabItem tab, TerminalControl terminal)
    {
        if (!_states.TryGetValue(tab, out var state) || state.Stopped) return;
        state.Stopped = true;
        if (state.Session is { } session)
        {
            session.StoppedCount++;
            state.StoppedCounted = true;
        }
        try
        {
            // если программа умерла в полноэкранном режиме (vim), сначала возвращаем обычный экран и курсор
            var leave = terminal.Terminal.IsAlternateBufferActive ? "\u001b[?1049l" : "";
            Emit(terminal, leave + "\u001b[?25h" + StoppedMessage());
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.F("Ошибка: {0}", ex.Message);
        }
        FocusTerminal(tab);
    }

    private async Task SaveOutputAsync(TerminalControl terminal, TabItem tab)
    {
        var buffer = terminal.Terminal.Buffer;
        var lines = new List<string>();
        for (var i = 0; i < buffer.Length; i++)
        {
            var line = buffer.GetLine(i);
            lines.Add(line is null ? "" : line.TranslateToString(true, 0, line.Length));
        }
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        var title = (((tab.Header as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault())?.Text ?? "session");
        foreach (var c in Path.GetInvalidFileNameChars()) title = title.Replace(c, '_');

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("Сохранить вывод терминала"),
            SuggestedFileName = $"{title}-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            DefaultExtension = "txt",
            FileTypeChoices = new[] { new FilePickerFileType(Loc.T("Текстовые файлы")) { Patterns = new[] { "*.txt" } } },
        });
        if (file is null) return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            await writer.WriteAsync(string.Join(Environment.NewLine, lines) + Environment.NewLine);
            StatusText.Text = Loc.F("Вывод сохранён: {0}", file.Name);
        }
        catch (Exception ex)
        {
            await Dialogs.Info(this, "Сохранить вывод терминала", Loc.F("Не удалось сохранить: {0}", ex.Message));
        }
    }

    /// <summary>Перезапуск: вкладка заменяется новой на том же месте и в той же области.</summary>
    /// <summary>Перезапуск в том же окне: прежний вывод остаётся, под ним черта и новый сеанс.</summary>
    private void RestartLocal(TabItem tab, TerminalControl terminal)
    {
        if (!_states.TryGetValue(tab, out var state) || !state.Stopped) return;
        state.Stopped = false;
        WriteRestartMarker(terminal);
        _ = terminal.LaunchProcess();
        FocusTerminal(tab);
    }

    /// <summary>Строка-разделитель между старым и новым сеансом в окне терминала.</summary>
    private static void WriteRestartMarker(TerminalControl terminal)
    {
        try
        {
            var esc = "\u001b";
            var leave = terminal.Terminal.IsAlternateBufferActive ? esc + "[?1049l" : "";
            terminal.Terminal.Write(leave + esc + "[0m" + esc + "[?25h\r\n" + esc + "[90m──── " + Loc.T("Сеанс перезапущен") + " ────" + esc + "[0m\r\n");
        }
        catch
        {
            // терминал ещё не готов: разделитель не критичен
        }
    }

    private void OnLocal(object? sender, RoutedEventArgs e) => OpenLocal();

    private void OpenLocal(int? position = null)
    {
        var terminal = TerminalFactory.Create("powershell.exe");
        var tab = AddTab("Local", TerminalFactory.Host(terminal), () => { try { terminal.Kill(); } catch { } }, position);
        WireStopKeys(tab, terminal);
        _states[tab].Restart = () => RestartLocal(tab, terminal);
        _states[tab].Duplicate = () => OpenLocal(Tabs.Items.IndexOf(tab) + 1);
        terminal.ProcessExited += (_, _) => Dispatcher.UIThread.Post(() => MarkStopped(tab, terminal));
    }

    // ---------- подключение ----------

    /// <summary>Вкладка, в которую подключается перезапущенный сеанс: терминал и его прежний вывод остаются.</summary>
    private sealed record ReuseTarget(TabItem Tab, TerminalControl Terminal, SessionView? View);

    private async void Connect(SessionItem s, int? position = null, ReuseTarget? reuse = null)
    {
        IPtyConnection? conn = null;
        var closed = false;
        var counted = false;
        SessionView? view_ = null;
        // строка сессии в дереве подсвечена зелёным, пока открыта хотя бы одна её вкладка
        void Release()
        {
            if (!counted) return;
            counted = false;
            s.ConnectionCount--;
        }

        void CloseTab()
        {
            // закрытие вкладки: если соединение ещё устанавливается, оно будет оборвано по готовности
            closed = true;
            Release();
            view_?.Detach();
            try { conn?.Kill(); } catch { }
        }

        // Окно консоли открывается сразу, ещё до подключения: в нём видно, что происходит, а при неудаче остаётся баннер с вариантами.
        TerminalControl terminal;
        SessionView? view;
        TabItem tab;
        if (reuse is null)
        {
            terminal = TerminalFactory.Create();
            // у SSH-вкладки есть проводник и панель состояния, у Telnet их нет
            view = s.Protocol == Protocol.Ssh ? new SessionView(terminal) : null;
            view_ = view;
            tab = AddTab(s.Name, (Control?)view ?? TerminalFactory.Host(terminal), CloseTab, position);
            WireStopKeys(tab, terminal);
            var created = tab;
            _states[tab].Restart = () => Reconnect(created, s);
            _states[tab].Duplicate = () => Connect(s, Tabs.Items.IndexOf(created) + 1);
            _states[tab].Session = s;
            // событие контрола приходит не всегда; соединение сообщает о конце само (см. ниже), это запасной путь
            terminal.ProcessExited += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (_states.TryGetValue(created, out var st)) st.OnEnded?.Invoke();
            });
            WriteStatus(terminal, Loc.F("Подключение к {0}:{1}…", s.Host, s.Port));
        }
        else
        {
            (tab, terminal, view) = (reuse.Tab, reuse.Terminal, reuse.View);
            view_ = view;
        }

        try
        {
            Func<Task<IPtyConnection>> open;

            if (s.Protocol == Protocol.Telnet)
            {
                open = async () => await TelnetConnection.ConnectAsync(s.Host, s.Port, 120, 30);
            }
            else
            {
                // пароль не сохранён — спрашиваем при подключении
                var password = s.Password;
                if (s.Auth == AuthKind.Password && password.Length == 0)
                {
                    var asked = await Dialogs.AskText(this, "Пароль", Loc.F("Пароль для {0}@{1}", s.Username, s.Host), password: true);
                    if (asked is null)
                    {
                        // отказ от ввода пароля: новая вкладка не нужна, у перезапускаемой всё остаётся как было
                        if (reuse is null)
                        {
                            CloseTab();
                            RemoveTab(tab);
                        }
                        return;
                    }
                    password = asked;
                }

                open = () => Task.Run<IPtyConnection>(() =>
                {
                    var methods = new List<AuthenticationMethod>();
                    if (s.Auth == AuthKind.Key)
                    {
                        methods.Add(new PrivateKeyAuthenticationMethod(s.Username,
                            new PrivateKeyFile(s.KeyPath, s.Passphrase.Length > 0 ? s.Passphrase : null)));
                    }
                    else
                    {
                        methods.Add(new PasswordAuthenticationMethod(s.Username, password));
                        // Многие серверы (VMware ESXi, часть Linux с PAM) принимают пароль только как keyboard-interactive:
                        // на запрос «Password:» отвечаем тем же паролем.
                        var interactive = new KeyboardInteractiveAuthenticationMethod(s.Username);
                        interactive.AuthenticationPrompt += (_, e) =>
                        {
                            foreach (var prompt in e.Prompts)
                                if (prompt.Request.Contains("assword", StringComparison.OrdinalIgnoreCase) || prompt.Request.Contains("ароль", StringComparison.OrdinalIgnoreCase))
                                    prompt.Response = password;
                        };
                        methods.Add(interactive);
                    }
                    var client = new SshClient(new ConnectionInfo(s.Host, s.Port, s.Username, methods.ToArray()));
                    HostKeyVerifier.Attach(client);
                    try
                    {
                        client.Connect();
                        return new SshShellConnection(client, 120, 30);
                    }
                    catch
                    {
                        client.Dispose();
                        throw;
                    }
                });
            }

            if (reuse is not null)
            {
                // перезапуск: прежний вывод остаётся, под ним черта и новое подключение
                var state = _states[tab];
                state.OnClose = CloseTab;
                if (state.StoppedCounted)
                {
                    s.StoppedCount--; // красная подсветка уходит: идёт новое подключение
                    state.StoppedCounted = false;
                }
                state.Stopped = false;
                WriteRestartMarker(terminal);
                WriteStatus(terminal, Loc.F("Подключение к {0}:{1}…", s.Host, s.Port));
            }

            StatusText.Text = Loc.F("Подключение к {0}…", s.Host);
            conn = await open();
            StatusText.Text = "";

            if (closed)
            {
                conn.Kill();
                return;
            }

            if (reuse is not null)
            {
                try { terminal.DetachConnection(); } catch { /* прежнего соединения уже нет */ }
            }
            else
            {
                // экран пуст, кроме нашей строки «Подключение…»: убираем её, дальше идёт вывод сервера
                Emit(terminal, "\u001b[2J\u001b[H");
            }

            if (view is not null && conn is SshShellConnection ssh)
            {
                view.AttachConnection(ssh);
                ssh.ResponsivenessChanged += responsive => Dispatcher.UIThread.Post(() =>
                    StatusText.Text = responsive ? "" : Loc.F("Нет ответа от {0}…", s.Host));
            }
            else
                terminal.AttachConnection(new HighlightingPtyConnection(conn));
            SyncSizeLater(conn, terminal);

            // Конец сеанса берём и от самого соединения: контрол терминала узнаёт об обрыве не всегда
            // (если чтение из мёртвого канала не завершилось, его событие не приходит).
            var attached = conn;
            void OnEnded()
            {
                if (!ReferenceEquals(conn, attached)) return; // событие от прежнего соединения
                StatusText.Text = ""; // «Нет ответа…» больше не актуально: сеанс завершён
                Release();
                view?.Detach();
                MarkStopped(tab, terminal);
            }
            _states[tab].OnEnded = OnEnded;
            conn.ProcessExited += (_, _) => Dispatcher.UIThread.Post(OnEnded);
            if (conn.WaitForExit(0)) Dispatcher.UIThread.Post(OnEnded); // соединение могло закончиться до подписки

            s.ConnectionCount++;
            counted = true;
            StatusText.Text = "";
            if (ReferenceEquals(Tabs.SelectedItem, tab))
            {
                // после подключения слева сразу файлы сервера, а клавиатура — в терминале
                if (view is not null) SetLeftMode(explorer: true);
                FocusTerminal(tab);
            }
            RefreshExplorer();
        }
        catch (Exception ex)
        {
            if (closed || !_states.ContainsKey(tab)) return; // вкладку уже закрыли
            // подключиться не удалось: вкладка остаётся, в ней причина и обычная подсказка «Enter / R / S»
            StatusText.Text = "";
            WriteStatus(terminal, Loc.F("Ошибка: {0}", ex.Message), error: true);
            MarkStopped(tab, terminal);
        }
    }

    /// <summary>Строка состояния в окне терминала (например, «Подключение к…» или текст ошибки).</summary>
    private static void WriteStatus(TerminalControl terminal, string text, bool error = false) =>
        Emit(terminal, (error ? "\u001b[91m" : "\u001b[90m") + text + "\u001b[0m\r\n");

    /// <summary>
    /// Пишет текст прямо в экран терминала. Пока контрол не показан (вкладка только что создана), запись пропала бы,
    /// поэтому она ждёт загрузки контрола; порядок записей сохраняется.
    /// </summary>
    private static void Emit(TerminalControl terminal, string text)
    {
        void Write()
        {
            try { terminal.Terminal.Write(text); }
            catch { /* терминал закрыт */ }
        }

        if (terminal.IsLoaded)
        {
            Write();
            return;
        }

        // Loaded приходит при каждом возвращении контрола на экран (переключение вкладок), а запись нужна один раз
        EventHandler<Avalonia.Interactivity.RoutedEventArgs>? handler = null;
        handler = (_, _) =>
        {
            terminal.Loaded -= handler;
            Write();
        };
        terminal.Loaded += handler;
    }

    /// <summary>
    /// Соединение открывается с размером 120×30, а окно терминала у́же или шире. Как только раскладка готова, сообщаем серверу
    /// настоящий размер, иначе строки на сервере переносятся по старой ширине (и после растягивания окна тоже).
    /// </summary>
    private void SyncSizeLater(IPtyConnection conn, TerminalControl terminal)
    {
        foreach (var delay in new[] { 250, 1000 })
        {
            DispatcherTimer.RunOnce(() =>
            {
                try
                {
                    var cols = terminal.Terminal.Cols;
                    var rows = terminal.Terminal.Rows;
                    if (cols > 0 && rows > 0 && conn.WaitForExit(0) == false) conn.Resize(cols, rows);
                }
                catch
                {
                    // соединение уже закрыто
                }
            }, TimeSpan.FromMilliseconds(delay));
        }
    }

    /// <summary>Перезапуск сеанса в том же окне терминала: прежний вывод сохраняется.</summary>
    private void Reconnect(TabItem tab, SessionItem session)
    {
        if (!_states.TryGetValue(tab, out var state) || !state.Stopped || state.Terminal is not { } terminal) return;
        Connect(session, null, new ReuseTarget(tab, terminal, tab.Tag as SessionView));
    }
}
