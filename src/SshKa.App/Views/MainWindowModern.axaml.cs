using Avalonia.Controls;
using SshKa.App.Storage;

namespace SshKa.App.Views;

// Modern-вариант главного окна: тёмная тема, рельс и сайдбар. Вся логика общая, в MainWindowBase;
// здесь только меню «⋯» в шапке сайдбара — импорт/экспорт/новая папка/локальный терминал.
public partial class MainWindowModern : MainWindowBase
{
    public MainWindowModern()
    {
        InitializeComponent();
        BuildOverflowMenu();
        InitShell();
    }

    /// <summary>Окно с уже открытым хранилищем (без запроса мастер-пароля): для проверки внешнего вида.</summary>
    internal MainWindowModern(SessionVault vault) : this()
    {
        SetVault(vault);
    }

    // свёрнутый сайдбар в Modern исчезает целиком: навигация остаётся в рельсе
    protected override double CollapsedPanelWidth => 0;
    protected override double PanelMinWidth => 200;
    protected override double PanelMaxWidth => 480;
    protected override double PanelDefaultWidth => 260;

    protected override void OnCollapsedChanged(bool collapsed) =>
        this.GetControl<DockPanel>("LeftPanel").IsVisible = !collapsed;

    private void BuildOverflowMenu()
    {
        var (import, export) = ImportExportSubmenus();
        var menu = new MenuFlyout();
        menu.Items.Add(import);
        menu.Items.Add(export);
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Новая папка", OnNewFolder));
        menu.Items.Add(MenuEntry("Локальный терминал", OnLocal));
        this.GetControl<Button>("OverflowButton").Flyout = menu;
    }
}
