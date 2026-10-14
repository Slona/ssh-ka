using System.Security.Cryptography;

namespace SshKa.App.Storage;

/// <summary>
/// Ключ хранилища, запомненный на этом компьютере: защищён средствами Windows (DPAPI) и привязан к вашей учётной записи.
/// Пока он есть, мастер-пароль при запуске не спрашивается. Вне Windows ключ не запоминается.
/// </summary>
public static class VaultKeyCache
{
    private static readonly byte[] Entropy = "SSH-ka vault key v1"u8.ToArray();

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SshKa", "vault.key");

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static bool Exists(string? path = null) => File.Exists(path ?? DefaultPath);

    public static void Save(byte[] key, string? path = null)
    {
        if (!IsSupported) return;
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
    }

    /// <summary>Запомненный ключ или null, если его нет или Windows не смогла его расшифровать.</summary>
    public static byte[]? TryLoad(string? path = null)
    {
        path ??= DefaultPath;
        if (!IsSupported || !File.Exists(path)) return null;
        try { return ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser); }
        catch { return null; }
    }

    public static void Delete(string? path = null)
    {
        try { File.Delete(path ?? DefaultPath); }
        catch { /* файла нет или он занят: при следующем запуске ключ всё равно не подойдёт */ }
    }
}
