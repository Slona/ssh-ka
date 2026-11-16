using System.Text;
using SshKa.App.Models;

namespace SshKa.App.Import;

/// <summary>
/// Пишет сессии в формат .mxtsessions: секция [Bookmarks] для корня и [Bookmarks_N] для папок,
/// внутри строки «Имя=#109#тип%хост%порт%логин%…» (тип 0 — SSH, 1 — Telnet). Файл читается и MobaXterm,
/// и нашим импортёром. Пароли в формат не входят: MobaXterm хранит их отдельно и в защищённом виде.
/// </summary>
public static class MobaXtermExporter
{
    public static ExportResult Write(IList<SessionItem> root)
    {
        var result = new ExportResult();
        var text = new StringBuilder();
        var section = 0;

        void WriteLevel(IList<SessionItem> level, string subRep)
        {
            var sessions = level.Where(i => !i.IsFolder).ToList();
            if (sessions.Count > 0)
            {
                text.Append('[').Append(section == 0 ? "Bookmarks" : $"Bookmarks_{section}").Append(']').AppendLine();
                text.Append("SubRep=").AppendLine(subRep);
                text.AppendLine("ImgNum=42");
                foreach (var s in sessions) text.AppendLine(Line(s));
                section++;
                result.Count += sessions.Count;
            }
            foreach (var folder in level.Where(i => i.IsFolder))
                WriteLevel(folder.Children, subRep.Length == 0 ? folder.Name : subRep + "\\" + folder.Name);
        }

        WriteLevel(root, "");
        result.Text = text.ToString();
        return result;
    }

    private static string Line(SessionItem s)
    {
        var type = s.Protocol == Protocol.Ssh ? "0" : "1";
        var key = s.Protocol == Protocol.Ssh && s.Auth == AuthKind.Key ? Clean(s.KeyPath) : "";
        // ключ пишем в пятое поле: импортёр ищет его по имени файла среди полей после логина
        return $"{Clean(s.Name)}=#109#{type}%{Clean(s.Host)}%{s.Port}%{Clean(s.Username)}%{key}%";
    }

    private static string Clean(string s) =>
        s.Replace("%", "_").Replace("#", "_").Replace("=", "_").Replace("\r", " ").Replace("\n", " ").Trim();
}
