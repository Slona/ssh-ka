using System.Text;
using SshKa.App.Localization;
using SshKa.App.Models;

namespace SshKa.App.Import;

/// <summary>
/// Пишет сессии в CSV: по строке на сессию, путь к папке — отдельной колонкой. Разделитель «;»,
/// чтобы файл сразу открывался в Excel с русской локалью; текст в кавычках при необходимости.
/// Пароли не экспортируются — в таблице их просто нет.
/// </summary>
public static class CsvExporter
{
    public static ExportResult Write(IList<SessionItem> root)
    {
        var result = new ExportResult();
        var text = new StringBuilder();
        text.AppendLine(string.Join(';',
            Loc.T("Папка"), Loc.T("Имя"), Loc.T("Протокол"), Loc.T("Хост"),
            Loc.T("Порт"), Loc.T("Логин"), Loc.T("Вход"), Loc.T("Файл ключа")));

        void Walk(IList<SessionItem> level, string path)
        {
            foreach (var item in level)
            {
                if (item.IsFolder)
                {
                    Walk(item.Children, path.Length == 0 ? item.Name : path + " / " + item.Name);
                    continue;
                }
                var cells = new[]
                {
                    path,
                    item.Name,
                    item.Protocol.ToString(),
                    item.Host,
                    item.Port.ToString(),
                    item.Username,
                    Loc.T(item.Auth == AuthKind.Key ? "Ключ" : "Пароль"),
                    item.Auth == AuthKind.Key ? item.KeyPath : "",
                };
                text.AppendLine(string.Join(';', cells.Select(Field)));
                result.Count++;
            }
        }

        Walk(root, "");
        result.Text = text.ToString();
        return result;
    }

    /// <summary>Ячейка: в кавычках, если внутри разделитель, кавычка или перевод строки.</summary>
    private static string Field(string value) =>
        value.Contains(';') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
}
