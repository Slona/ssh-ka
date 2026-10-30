using Avalonia;

namespace SshKa.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // причину внезапного закрытия программы пишем в тот же журнал, что и обрывы соединений
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Terminal.ConnectionLog.Write("crash", (e.IsTerminating ? "fatal: " : "unhandled: ") + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Terminal.ConnectionLog.Write("task", e.Exception.ToString());
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();
}
