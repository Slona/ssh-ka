using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Konscious.Security.Cryptography;
using SshKa.App.Models;

namespace SshKa.App.Storage;

/// <summary>
/// Дерево сессий в одном зашифрованном файле: Argon2id (мастер-пароль → ключ) + AES-256-GCM.
/// Шифруется весь файл целиком, включая имена хостов и логины.
/// </summary>
public sealed class SessionVault
{
    private const int Version = 1;
    private const int KeySize = 32;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private byte[] _key;
    private KdfParams _kdf;

    public ObservableCollection<SessionItem> Root { get; } = new();

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SshKa", "vault.json");

    private SessionVault(string path, byte[] key, KdfParams kdf)
    {
        _path = path;
        _key = key;
        _kdf = kdf;
    }

    public static SessionVault Create(string path, string masterPassword)
    {
        var kdf = new KdfParams { Salt = RandomNumberGenerator.GetBytes(SaltSize) };
        var vault = new SessionVault(path, Derive(masterPassword, kdf), kdf);
        vault.Save();
        return vault;
    }

    /// <exception cref="CryptographicException">Неверный мастер-пароль или повреждённый файл.</exception>
    public static SessionVault Unlock(string path, string masterPassword)
    {
        var file = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(path), Json)
                   ?? throw new InvalidDataException("Пустой файл хранилища");
        if (file.Version != Version)
            throw new InvalidDataException($"Неподдерживаемая версия хранилища: {file.Version}");

        return Open(path, file, Derive(masterPassword, file.Kdf));
    }

    /// <summary>Открывает хранилище готовым ключом (запомненным на этом компьютере), без пароля.</summary>
    /// <exception cref="CryptographicException">Ключ не подходит или файл повреждён.</exception>
    public static SessionVault UnlockWithKey(string path, byte[] key)
    {
        var file = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(path), Json)
                   ?? throw new InvalidDataException("Пустой файл хранилища");
        if (file.Version != Version)
            throw new InvalidDataException($"Неподдерживаемая версия хранилища: {file.Version}");
        return Open(path, file, key);
    }

    private static SessionVault Open(string path, VaultFile file, byte[] key)
    {
        var plain = new byte[file.Data.Length];
        using (var aes = new AesGcm(key, TagSize))
            aes.Decrypt(file.Nonce, file.Data, file.Tag, plain);

        var vault = new SessionVault(path, key, file.Kdf);
        var items = JsonSerializer.Deserialize<List<SessionItem>>(plain, Json) ?? new();
        foreach (var i in items) vault.Root.Add(i);
        return vault;
    }

    /// <summary>Копия ключа шифрования (для запоминания на этом компьютере).</summary>
    public byte[] ExportKey() => (byte[])_key.Clone();

    /// <summary>Совпадает ли пароль с мастер-паролем этого хранилища.</summary>
    public bool VerifyPassword(string password) =>
        CryptographicOperations.FixedTimeEquals(Derive(password, _kdf), _key);

    /// <summary>Меняет мастер-пароль: новая соль, новый ключ, файл перешифровывается.</summary>
    /// <exception cref="CryptographicException">Текущий пароль неверен.</exception>
    public void ChangePassword(string current, string newPassword)
    {
        if (!VerifyPassword(current)) throw new CryptographicException("Неверный мастер-пароль");
        var kdf = new KdfParams { Salt = RandomNumberGenerator.GetBytes(SaltSize) };
        _key = Derive(newPassword, kdf);
        _kdf = kdf;
        Save();
    }

    public void Save()
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(Root, Json);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var data = new byte[plain.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(_key, TagSize))
            aes.Encrypt(nonce, plain, data, tag);

        var file = new VaultFile { Version = Version, Kdf = _kdf, Nonce = nonce, Tag = tag, Data = data };

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json));
        if (File.Exists(_path))
            File.Replace(tmp, _path, _path + ".bak");
        else
            File.Move(tmp, _path);
    }

    /// <summary>Коллекция, в которой лежит узел, или null.</summary>
    public ObservableCollection<SessionItem>? FindContainer(SessionItem item) => FindContainer(Root, item);

    private static ObservableCollection<SessionItem>? FindContainer(ObservableCollection<SessionItem> level, SessionItem item)
    {
        if (level.Contains(item)) return level;
        foreach (var f in level.Where(x => x.IsFolder))
            if (FindContainer(f.Children, item) is { } found) return found;
        return null;
    }

    private static byte[] Derive(string password, KdfParams p)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = p.Salt,
            MemorySize = p.MemoryKb,
            Iterations = p.Iterations,
            DegreeOfParallelism = p.Parallelism,
        };
        return argon.GetBytes(KeySize);
    }

    private sealed class KdfParams
    {
        public byte[] Salt { get; set; } = Array.Empty<byte>();
        public int MemoryKb { get; set; } = 65536;
        public int Iterations { get; set; } = 3;
        public int Parallelism { get; set; } = 2;
    }

    private sealed class VaultFile
    {
        public int Version { get; set; }
        public KdfParams Kdf { get; set; } = new();
        public byte[] Nonce { get; set; } = Array.Empty<byte>();
        public byte[] Tag { get; set; } = Array.Empty<byte>();
        public byte[] Data { get; set; } = Array.Empty<byte>();
    }
}
