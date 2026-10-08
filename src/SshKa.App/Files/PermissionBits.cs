namespace SshKa.App.Files;

/// <summary>Права доступа Unix: перевод между числом (0644), строкой «rw-r--r--» и разбором вывода ls.</summary>
public static class PermissionBits
{
    private const string Letters = "rwxrwxrwx";

    /// <summary>Строка в стиле ls: «drwxr-xr-x». setuid, setgid и sticky показываются как s, s и t.</summary>
    public static string ToText(int mode, bool isDirectory, bool isSymlink = false)
    {
        var chars = new char[10];
        chars[0] = isSymlink ? 'l' : isDirectory ? 'd' : '-';
        for (var i = 0; i < 9; i++)
            chars[i + 1] = (mode & (0x100 >> i)) != 0 ? Letters[i] : '-';

        if ((mode & 0x800) != 0) chars[3] = chars[3] == 'x' ? 's' : 'S';
        if ((mode & 0x400) != 0) chars[6] = chars[6] == 'x' ? 's' : 'S';
        if ((mode & 0x200) != 0) chars[9] = chars[9] == 'x' ? 't' : 'T';
        return new string(chars);
    }

    /// <summary>Разбирает 9 или 10 символов вида «rwxr-xr-x» / «drwxr-xr-x» (из вывода ls). -1, если строка не похожа на права.</summary>
    public static int Parse(string text)
    {
        if (text.Length == 10) text = text[1..];
        if (text.Length != 9) return -1;

        var mode = 0;
        for (var i = 0; i < 9; i++)
        {
            var c = text[i];
            if (c == '-') continue;
            // в позиции x допустимы x, s, t (с установленным битом) и S, T (без него)
            var isExec = i % 3 == 2;
            if (c == Letters[i] || (isExec && c is 's' or 't')) mode |= 0x100 >> i;
            else if (!(isExec && c is 'S' or 'T')) return -1;

            if (i == 2 && c is 's' or 'S') mode |= 0x800;
            if (i == 5 && c is 's' or 'S') mode |= 0x400;
            if (i == 8 && c is 't' or 'T') mode |= 0x200;
        }
        return mode;
    }

    public static string ToOctal(int mode) => Convert.ToString(mode & 0xFFF, 8).PadLeft(3, '0');

    public static bool TryParseOctal(string? text, out int mode)
    {
        mode = 0;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 4 || text.Any(c => c is < '0' or > '7')) return false;
        mode = Convert.ToInt32(text, 8);
        return true;
    }
}
