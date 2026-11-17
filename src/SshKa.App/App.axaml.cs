using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SshKa.App.Storage;
using SshKa.App.Views;

namespace SshKa.App;

public partial class App : Application
{
    /// <summary>Вариант интерфейса Modern (тёмный, в стиле Termius). Задаётся до создания окон,
    /// меняется через настройки с перезапуском; от него зависят и тёмные диалоги.</summary>
    public static bool ModernUi { get; private set; }

    /// <summary>Для тестового рендера (UiPreview): включает Modern без чтения настроек.</summary>
    internal static void UseModernUi() => ModernUi = true;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        ModernUi = AppSettings.Current.Ui == "modern";
        UiTheme.ApplyApplication();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = ModernUi ? new MainWindowModern() : new MainWindow();

        base.OnFrameworkInitializationCompleted();
    }
}
