using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SshKa.App.Models;

public enum Protocol { Ssh, Telnet }

public enum AuthKind { Password, Key }

/// <summary>Куда придётся перетаскиваемый узел относительно строки под курсором.</summary>
public enum DropIndicator { None, Before, After, Into }

/// <summary>Узел дерева: либо папка (IsFolder, есть Children), либо сессия.</summary>
public partial class SessionItem : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _name = "";

    public bool IsFolder { get; set; }

    // Свойство без сеттера: System.Text.Json по умолчанию его игнорирует при чтении, и содержимое папок терялось бы
    // при каждом запуске. Populate заставляет заполнять уже созданную коллекцию.
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public ObservableCollection<SessionItem> Children { get; } = new();

    public Protocol Protocol { get; set; } = Protocol.Ssh;
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _username = "";

    public AuthKind Auth { get; set; } = AuthKind.Password;
    public string KeyPath { get; set; } = "";

    // Секреты лежат только внутри зашифрованного хранилища.
    public string Password { get; set; } = "";
    public string Passphrase { get; set; } = "";

    /// <summary>Папка раскрыта в дереве. Сохраняется вместе с остальным, чтобы дерево открывалось как было.</summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>Сколько вкладок с этой сессией открыто сейчас; пока больше нуля, строка подсвечивается зелёным.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [property: JsonIgnore]
    private int _connectionCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDropBefore), nameof(IsDropAfter), nameof(IsDropInto))]
    [property: JsonIgnore]
    private DropIndicator _dropIndicator;

    [JsonIgnore] public bool IsConnected => ConnectionCount > 0;
    [JsonIgnore] public bool IsDropBefore => DropIndicator == DropIndicator.Before;
    [JsonIgnore] public bool IsDropAfter => DropIndicator == DropIndicator.After;
    [JsonIgnore] public bool IsDropInto => DropIndicator == DropIndicator.Into;

    /// <summary>Подпись в дереве: для сессии после имени в скобках идёт сохранённый логин.</summary>
    [JsonIgnore]
    public string DisplayName => IsFolder || string.IsNullOrEmpty(Username) ? Name : $"{Name} ({Username})";

    /// <summary>true, если node лежит где-то внутри этого узла (на любой глубине).</summary>
    public bool Contains(SessionItem node) =>
        Children.Any(c => c == node || c.Contains(node));
}
