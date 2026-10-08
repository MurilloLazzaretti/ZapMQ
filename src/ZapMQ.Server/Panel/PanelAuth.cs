using System.Security.Cryptography;
using System.Text;

namespace ZapMQ.Server.Panel;

/// <summary>
/// The login of the panel: one user and password from the settings, and a signed cookie that
/// stands for the session. Everything that decides who may get in is in this class, so the
/// way of logging in can be replaced without touching the rest.
/// </summary>
public sealed class PanelAuth(PanelOptions options, TimeProvider time)
{
    public const string Cookie = "zapmq_session";

    // A new key at every start: restarting the service ends the sessions.
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public bool HasDefaultPassword => options.HasDefaultPassword;

    public TimeSpan SessionLength => TimeSpan.FromHours(Math.Max(1, options.SessionHours));

    public bool Accepts(string? user, string? password) =>
        user is not null && password is not null
        && FixedTimeEquals(user, options.User)
        & FixedTimeEquals(password, options.Password);

    /// <summary>
    /// The contents of the session cookie for a user who has just logged in.
    /// </summary>
    public string Issue(string user)
    {
        var payload = $"{Convert.ToBase64String(Encoding.UTF8.GetBytes(user))}.{(time.GetUtcNow() + SessionLength).ToUnixTimeSeconds()}";
        return $"{payload}.{Sign(payload)}";
    }

    /// <summary>
    /// The user a session cookie belongs to, or null when it is not a valid one anymore.
    /// </summary>
    public string? Validate(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        var cut = token.LastIndexOf('.');
        if (cut <= 0 || !FixedTimeEquals(Sign(token[..cut]), token[(cut + 1)..]))
            return null;

        var parts = token[..cut].Split('.');
        if (parts.Length != 2 || !long.TryParse(parts[1], out var expires) || expires < time.GetUtcNow().ToUnixTimeSeconds())
            return null;

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private string Sign(string payload) =>
        Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload)));

    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
}
