using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZapMQ.Server.Panel;

/// <summary>
/// Sealing and opening what goes over the administration queue of the Worker Control:
/// AES-256-GCM with a key only the two ends have. What is sealed is the base64 of the nonce
/// (12 bytes), the tag (16) and the text. The Worker Control does the same, written apart.
/// </summary>
public static class AdminSeal
{
    public const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public static string Seal(byte[] key, string plain, byte[]? nonce = null)
    {
        nonce ??= RandomNumberGenerator.GetBytes(NonceBytes);
        var text = Encoding.UTF8.GetBytes(plain);
        var sealedBytes = new byte[NonceBytes + TagBytes + text.Length];
        nonce.CopyTo(sealedBytes, 0);
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, text, sealedBytes.AsSpan(NonceBytes + TagBytes), sealedBytes.AsSpan(NonceBytes, TagBytes));
        return Convert.ToBase64String(sealedBytes);
    }

    /// <summary>
    /// Null when it was not sealed with this key, or was changed on the way.
    /// </summary>
    public static string? Open(byte[] key, string sealedText)
    {
        try
        {
            var bytes = Convert.FromBase64String(sealedText);
            if (bytes.Length < NonceBytes + TagBytes)
                return null;
            var plain = new byte[bytes.Length - NonceBytes - TagBytes];
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(bytes.AsSpan(0, NonceBytes), bytes.AsSpan(NonceBytes + TagBytes), bytes.AsSpan(NonceBytes, TagBytes), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// The key this ZapMQ seals with what it asks of the Worker Control. Beside it, the one that
/// was sent and not yet confirmed: if the answer to "take this key" is lost, the next request
/// tries it and nobody is locked out.
/// </summary>
public sealed class AdminKeyStore
{
    private sealed class Kept
    {
        public string? Key { get; set; }
        public string? Pending { get; set; }
    }

    private readonly string _path;
    private readonly ILogger<AdminKeyStore> _logger;
    private readonly object _gate = new();
    private Kept _kept = new();

    public AdminKeyStore(string path, ILogger<AdminKeyStore> logger)
    {
        _path = path;
        _logger = logger;
        try
        {
            if (File.Exists(path))
                _kept = JsonSerializer.Deserialize<Kept>(File.ReadAllText(path)) ?? new Kept();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogError("The administration key in {Path} could not be read: {Error}", path, error.Message);
        }
    }

    private static byte[]? Bytes(string? text)
    {
        try
        {
            return text is { Length: > 0 } && Convert.FromBase64String(text) is { Length: AdminSeal.KeyBytes } key ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public byte[]? Key
    {
        get
        {
            lock (_gate)
                return Bytes(_kept.Key);
        }
    }

    public byte[]? Pending
    {
        get
        {
            lock (_gate)
                return Bytes(_kept.Pending);
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_kept));
        File.Move(temporary, _path, overwrite: true);
    }

    /// <summary>
    /// A new key, kept as not yet confirmed before it is sent anywhere.
    /// </summary>
    public byte[] Propose()
    {
        lock (_gate)
        {
            var key = RandomNumberGenerator.GetBytes(AdminSeal.KeyBytes);
            _kept.Pending = Convert.ToBase64String(key);
            Save();
            return key;
        }
    }

    /// <summary>
    /// The Worker Control answered with this key: it is the one from now on.
    /// </summary>
    public void Confirm(byte[] key)
    {
        lock (_gate)
        {
            _kept.Key = Convert.ToBase64String(key);
            _kept.Pending = null;
            Save();
        }
    }

    /// <summary>
    /// A key that was sent and refused is not tried again.
    /// </summary>
    public void Withdraw()
    {
        lock (_gate)
        {
            _kept.Pending = null;
            Save();
        }
    }
}
