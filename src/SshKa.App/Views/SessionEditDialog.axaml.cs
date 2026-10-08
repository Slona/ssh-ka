using SshKa.App.Localization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SshKa.App.Models;

namespace SshKa.App.Views;

/// <summary>Редактор сессии. Изменения записываются в item только по «Сохранить».</summary>
public partial class SessionEditDialog : Window
{
    private readonly SessionItem _item = new();
    // Пока строится форма, SelectionChanged срабатывает раньше, чем создаются остальные поля.
    private bool _loading = true;

    public SessionEditDialog()
    {
        InitializeComponent();
        Loc.Apply(this);
        _loading = false;
        UpdateVisibility();
    }

    public SessionEditDialog(SessionItem item) : this()
    {
        _item = item;
        _loading = true;
        NameBox.Text = item.Name;
        ProtoBox.SelectedIndex = (int)item.Protocol;
        HostBox.Text = item.Host;
        PortBox.Text = item.Port.ToString();
        UserBox.Text = item.Username;
        AuthBox.SelectedIndex = (int)item.Auth;
        KeyBox.Text = item.KeyPath;
        PassBox.Text = item.Password;
        PhraseBox.Text = item.Passphrase;
        _loading = false;
        UpdateVisibility();
    }

    private void OnProtocolChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        // подставляем стандартный порт, только если пользователь не менял его вручную
        if (ProtoBox.SelectedIndex == (int)Protocol.Telnet && PortBox.Text == "22") PortBox.Text = "23";
        if (ProtoBox.SelectedIndex == (int)Protocol.Ssh && PortBox.Text == "23") PortBox.Text = "22";
        UpdateVisibility();
    }

    private void OnAuthChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_loading) UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        if (AuthBox is null || ProtoBox is null) return;
        var ssh = ProtoBox.SelectedIndex == (int)Protocol.Ssh;
        var key = ssh && AuthBox.SelectedIndex == (int)AuthKind.Key;

        AuthLabel.IsVisible = AuthBox.IsVisible = ssh;
        KeyLabel.IsVisible = KeyRow.IsVisible = key;
        PhraseLabel.IsVisible = PhraseBox.IsVisible = key;
        // для telnet и входа по паролю нужен пароль, для ключа он не нужен
        PassLabel.IsVisible = PassBox.IsVisible = !key;
    }

    private async void OnBrowseKey(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("Приватный ключ"),
            AllowMultiple = false,
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            KeyBox.Text = path;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        var host = HostBox.Text?.Trim() ?? "";
        if (name.Length == 0) name = host;
        if (host.Length == 0) { ErrorText.Text = Loc.T("Укажите хост"); return; }
        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535) { ErrorText.Text = Loc.T("Порт: число от 1 до 65535"); return; }

        var ssh = ProtoBox.SelectedIndex == (int)Protocol.Ssh;
        var auth = ssh ? (AuthKind)AuthBox.SelectedIndex : AuthKind.Password;
        if (auth == AuthKind.Key && string.IsNullOrWhiteSpace(KeyBox.Text)) { ErrorText.Text = Loc.T("Укажите файл ключа"); return; }

        _item.Name = name;
        _item.Protocol = (Protocol)ProtoBox.SelectedIndex;
        _item.Host = host;
        _item.Port = port;
        _item.Username = UserBox.Text?.Trim() ?? "";
        _item.Auth = auth;
        _item.KeyPath = auth == AuthKind.Key ? KeyBox.Text!.Trim() : "";
        _item.Password = auth == AuthKind.Password ? PassBox.Text ?? "" : "";
        _item.Passphrase = auth == AuthKind.Key ? PhraseBox.Text ?? "" : "";
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
