using System.Text.RegularExpressions;

namespace SshKa.App.Highlight;

/// <summary>
/// Язык для раскраски кода в редакторах: один токенизатор-регулярка, у которой именованные группы
/// означают тип токена (c — комментарий, s — строка, k — ключевое слово, b — встроенное/константа,
/// n — число, v — переменная). Совпадение ищется слева направо, поэтому # внутри строки комментарием не считается.
/// </summary>
public sealed class CodeLanguage
{
    public CodeLanguage(string name, Regex tokenizer)
    {
        Name = name;
        Tokenizer = tokenizer;
    }

    public string Name { get; }
    public Regex Tokenizer { get; }
}

public static class CodeLanguages
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(50);

    private static readonly Dictionary<string, Lazy<CodeLanguage>> ByName = new();
    private static readonly Dictionary<string, string> ExtensionToName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> FileNameToName = new(StringComparer.OrdinalIgnoreCase);

    static CodeLanguages()
    {
        Add("python", new[] { "py", "pyw" }, () => Build("python", "#", false, "\"'",
            "and as assert async await break class continue def del elif else except finally for from global if import in is lambda nonlocal not or pass raise return try while with yield match case",
            "True False None self cls print len range int str float list dict set tuple open isinstance super",
            extra: new[] { @"@[A-Za-z_][\w.]*" }));

        Add("shell", new[] { "sh", "bash", "zsh", "ksh", "profile", "bashrc", "zshrc" }, () => Build("shell", "#", false, "\"'",
            "if then else elif fi for while until do done case esac in function select return exit break continue local export readonly declare unset shift source alias",
            "echo printf cd read test true false eval exec set trap",
            shellVars: true));

        Add("javascript", new[] { "js", "mjs", "cjs", "jsx", "ts", "tsx" }, () => Build("javascript", "//", true, "\"'`",
            "break case catch class const continue debugger default delete do else export extends finally for function if import in instanceof let new of return static super switch this throw try typeof var void while with yield async await from as interface type enum implements public private protected readonly namespace declare abstract",
            "true false null undefined NaN Infinity console"));

        Add("c", new[] { "c", "h", "cc", "cpp", "cxx", "hpp", "hh", "ino" }, () => Build("c", "//", true, "\"'",
            "auto break case char class const continue default do double else enum extern float for goto if inline int long namespace new operator private protected public register return short signed sizeof static struct switch template this throw try typedef typename union unsigned using virtual void volatile while catch delete bool",
            "true false NULL nullptr",
            extra: new[] { @"#\s*(?:include|define|undef|ifdef|ifndef|if|elif|else|endif|pragma|error)\b" }));

        Add("go", new[] { "go" }, () => Build("go", "//", true, "\"'`",
            "break case chan const continue default defer else fallthrough for func go goto if import interface map package range return select struct switch type var",
            "true false nil iota"));

        Add("rust", new[] { "rs" }, () => Build("rust", "//", true, "\"",
            "as async await break const continue crate dyn else enum extern fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait type unsafe use where while",
            "true false None Some Ok Err"));

        Add("java", new[] { "java", "cs", "kt", "kts", "scala", "swift", "dart" }, () => Build("java", "//", true, "\"'",
            "abstract assert boolean break byte case catch char class const continue default do double else enum extends final finally float for if implements import instanceof int interface long namespace new package private protected public return short static super switch synchronized this throw throws try void volatile while var val fun using string object async await foreach in is as override",
            "true false null"));

        Add("php", new[] { "php" }, () => Build("php", "//|#", true, "\"'",
            "function class public private protected static return if else elseif foreach for while switch case break continue echo new namespace use try catch throw extends implements abstract final interface trait const global require include require_once include_once",
            "true false null TRUE FALSE NULL",
            extra: new[] { @"\$[A-Za-z_]\w*" }));

        Add("ruby", new[] { "rb", "rake", "gemspec" }, () => Build("ruby", "#", false, "\"'",
            "def end class module if elsif else unless while until for in do begin rescue ensure return yield require include extend attr_accessor attr_reader attr_writer puts then case when",
            "true false nil self"));

        Add("lua", new[] { "lua" }, () => Build("lua", "--", false, "\"'",
            "function end if then else elseif for while do repeat until return local break and or not in",
            "true false nil"));

        Add("sql", new[] { "sql" }, () => Build("sql", "--", true, "'\"",
            "select from where insert into values update set delete create table drop alter add join left right inner outer on group by order having limit offset as and or not null primary key foreign references index distinct union all case when then else end like in exists between is unique default",
            "true false", ignoreCase: true));

        Add("json", new[] { "json", "jsonc", "json5" }, () => Build("json", null, false, "\"", "", "true false null"));

        Add("yaml", new[] { "yaml", "yml" }, () => Build("yaml", "#", false, "\"'", "",
            "true false null yes no on off True False Null",
            extra: new[] { @"(?<![\w.-])[\w.-]+(?=:(?:\s|$))", @"(?<=^[ \t]*)- " }));

        Add("ini", new[] { "ini", "cfg", "env", "service", "socket", "timer", "desktop", "toml", "properties", "editorconfig" },
            () => Build("ini", "#|;", false, "\"'", "", "true false yes no on off",
                extra: new[] { @"(?<=^[ \t]*)\[[^\]\r\n]+\]", @"(?<=^[ \t]*)[\w.-]+(?=[ \t]*=)" }));

        Add("conf", new[] { "conf", "config", "cnf", "rules" }, () => Build("conf", "#", false, "\"'", "",
            "true false yes no on off enabled disabled",
            shellVars: true,
            extra: new[] { @"(?<=^[ \t]*)[A-Za-z_][\w.-]*(?=[ \t]+\S|[ \t]*;|[ \t]*\{)" }));

        Add("html", new[] { "html", "htm", "xml", "xhtml", "svg", "vue" }, () => new CodeLanguage("html", new Regex(
            @"(?<c><!--.*?-->)|(?<s>""[^""]*""|'[^']*')|(?<k></?[A-Za-z][\w:.-]*|/?>)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant, Timeout)));

        Add("docker", Array.Empty<string>(), () => Build("docker", "#", false, "\"'",
            "FROM RUN CMD LABEL EXPOSE ENV ADD COPY ENTRYPOINT VOLUME USER WORKDIR ARG ONBUILD STOPSIGNAL HEALTHCHECK SHELL AS", "",
            shellVars: true));

        // файлы без расширения
        foreach (var n in new[] { ".bashrc", ".bash_profile", ".bash_aliases", ".zshrc", ".profile" }) FileNameToName[n] = "shell";
        FileNameToName["Dockerfile"] = "docker";
        FileNameToName["Makefile"] = "conf";
        FileNameToName["crontab"] = "conf";
        FileNameToName["fstab"] = "conf";
        FileNameToName["hosts"] = "conf";
        FileNameToName["sshd_config"] = "conf";
        FileNameToName[".gitconfig"] = "ini";
        FileNameToName[".env"] = "ini";

        ByName["generic"] = new Lazy<CodeLanguage>(() => Build("generic", "#|//", true, "\"'", "", "true false null"));
    }

    private static void Add(string name, string[] extensions, Func<CodeLanguage> factory)
    {
        ByName[name] = new Lazy<CodeLanguage>(factory);
        foreach (var e in extensions) ExtensionToName[e] = name;
    }

    /// <summary>Язык по имени файла; для незнакомых расширений — универсальный (комментарии, строки, числа).</summary>
    public static CodeLanguage ForFile(string? path) => ByName[NameFor(path) ?? "generic"].Value;

    /// <summary>true, если язык удалось определить по имени файла (а не взят универсальный).</summary>
    public static bool IsKnown(string? path) => NameFor(path) is not null;

    private static string? NameFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var file = path.Replace('\\', '/');
        file = file[(file.LastIndexOf('/') + 1)..];
        if (FileNameToName.TryGetValue(file, out var byName)) return byName;
        var dot = file.LastIndexOf('.');
        if (dot >= 0 && ExtensionToName.TryGetValue(file[(dot + 1)..], out var byExt)) return byExt;
        return null;
    }

    private static string Strings(string quotes)
    {
        var alts = new List<string>();
        foreach (var q in quotes)
        {
            var e = Regex.Escape(q.ToString());
            alts.Add($@"{e}(?:\\.|[^{e}\\])*{e}"); // только завершённые строки: незакрытая кавычка чаще апостроф в тексте
        }
        return string.Join("|", alts);
    }

    private static CodeLanguage Build(string name, string? lineComment, bool blockComments, string quotes,
        string keywords, string builtins, bool ignoreCase = false, bool shellVars = false, string[]? extra = null)
    {
        var parts = new List<string>();

        var comments = new List<string>();
        if (lineComment is not null) comments.Add($"(?:{lineComment}).*");
        if (blockComments) comments.Add(@"/\*.*?\*/");
        if (comments.Count > 0) parts.Add($"(?<c>{string.Join("|", comments)})");

        if (quotes.Length > 0) parts.Add($"(?<s>{Strings(quotes)})");
        if (shellVars) parts.Add(@"(?<v>\$\{[^}]*\}|\$[A-Za-z_]\w*|\$[0-9@#?!*$])");

        foreach (var pattern in extra ?? Array.Empty<string>()) parts.Add($"(?<k>{pattern})");

        var b = builtins.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (b.Length > 0) parts.Add($@"(?<b>\b(?:{string.Join("|", b)})\b)");
        var k = keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (k.Length > 0) parts.Add($@"(?<k>\b(?:{string.Join("|", k)})\b)");

        parts.Add(@"(?<n>\b0[xX][0-9a-fA-F]+\b|\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b)");

        var options = RegexOptions.Compiled | RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        return new CodeLanguage(name, new Regex(string.Join("|", parts), options, Timeout));
    }
}
