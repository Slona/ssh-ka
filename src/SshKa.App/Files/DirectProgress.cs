namespace SshKa.App.Files;

/// <summary>
/// Передаёт прогресс библиотеки (SSH.NET) дальше без переключения контекста: колбэки приходят из потока
/// передачи, а marshalling в UI-поток и троттлинг делает приёмник (UiProgress панели файлов).
/// Обёртка Progress&lt;T&gt; здесь не подходит: она только добавляет лишний пост на каждый чанк передачи.
/// </summary>
internal sealed class DirectProgress<TIn, TOut>(Func<TIn, TOut> convert, IProgress<TOut> sink) : IProgress<TIn>
{
    public void Report(TIn value) => sink.Report(convert(value));
}
