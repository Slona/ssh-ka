using SshKa.App.Storage;

namespace SshKa.App.Views;

// Классический вариант главного окна: вся логика общая, в MainWindowBase (там же — Modern).
public partial class MainWindow : MainWindowBase
{
    public MainWindow()
    {
        InitializeComponent();
        InitShell();
    }

    /// <summary>Окно с уже открытым хранилищем (без запроса мастер-пароля): для проверки внешнего вида.</summary>
    internal MainWindow(SessionVault vault) : this()
    {
        SetVault(vault);
    }
}
