using Avalonia;
using Avalonia.Data.Converters;

namespace SshKa.App.Views;

/// <summary>Отступ слева для вложенных узлов дерева сессий: чем глубже уровень, тем правее.</summary>
public static class TreeIndent
{
    /// <summary>Ширина одного уровня вложенности (чуть больше стрелки раскрытия, чтобы сессии в папке были заметно правее).</summary>
    public const double Step = 20;

    public static readonly IValueConverter Converter = new FuncValueConverter<int, Thickness>(level => new Thickness(level * Step, 0, 0, 0));
}
