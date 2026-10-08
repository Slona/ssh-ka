using SshKa.App.Localization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SshKa.App.Files;

namespace SshKa.App.Views;

/// <summary>Окно прав доступа: чекбоксы, строка «rw-r--r--» и восьмеричный код всегда согласованы между собой.</summary>
public partial class PermissionsDialog : Window
{
    private readonly CheckBox[] _boxes;
    private int _special;      // setuid, setgid, sticky: окно их не меняет, но и не теряет
    private bool _loading = true;

    public PermissionsDialog()
    {
        InitializeComponent();
        Loc.Apply(this);
        _boxes = new[]
        {
            UserRead, UserWrite, UserExec,
            GroupRead, GroupWrite, GroupExec,
            OtherRead, OtherWrite, OtherExec,
        };
        foreach (var b in _boxes) b.IsCheckedChanged += OnBoxChanged;
        _loading = false;
    }

    public PermissionsDialog(string heading, int mode, bool isDirectory) : this()
    {
        Heading.Text = heading;
        _special = mode & 0xE00;
        SetMode(mode & 0x1FF, isDirectory, updateOctal: true);
        _isDirectory = isDirectory;
    }

    private bool _isDirectory;

    /// <summary>Расставляет чекбоксы, строку прав и код по числу.</summary>
    private void SetMode(int mode, bool isDirectory, bool updateOctal)
    {
        _loading = true;
        for (var i = 0; i < 9; i++) _boxes[i].IsChecked = (mode & (0x100 >> i)) != 0;
        TextBoxPerms.Text = PermissionBits.ToText(mode, false)[1..];
        if (updateOctal) OctalBox.Text = PermissionBits.ToOctal(mode);
        ErrorText.Text = "";
        _loading = false;
    }

    private int ModeFromBoxes()
    {
        var mode = 0;
        for (var i = 0; i < 9; i++)
            if (_boxes[i].IsChecked == true) mode |= 0x100 >> i;
        return mode;
    }

    private void OnBoxChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SetMode(ModeFromBoxes(), _isDirectory, updateOctal: true);
    }

    private void OnOctalChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (PermissionBits.TryParseOctal(OctalBox.Text, out var mode))
        {
            _special = mode & 0xE00;
            SetMode(mode & 0x1FF, _isDirectory, updateOctal: false); // сам код не переписываем, пока пользователь печатает
        }
        else
        {
            ErrorText.Text = Loc.T("Код: от одной до четырёх цифр 0–7");
        }
    }

    /// <summary>Итоговые права: чекбоксы вместе с нетронутыми специальными битами.</summary>
    private void OnApply(object? sender, RoutedEventArgs e)
    {
        if (!PermissionBits.TryParseOctal(OctalBox.Text, out _))
        {
            ErrorText.Text = Loc.T("Код: от одной до четырёх цифр 0–7");
            return;
        }
        Close((int?)(ModeFromBoxes() | _special));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close((int?)null);
}
