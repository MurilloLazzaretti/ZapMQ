using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ZapMQ.Server.Panel;

/// <summary>
/// The login of the panel: the users of <see cref="PanelUserStore"/> and a signed cookie that
/// stands for the session. Everything that decides who may get in is in this class, so the
/// way of logging in can be replaced without touching the rest.
/// </summary>
public sealed class PanelAuth(PanelOptions options, PanelUserStore users, TimeProvider time)
{
    public const string Cookie = "zapmq_session";

    /// <summary>
    /// How many wrong passwords in a row before a login has to wait, and for how long.
    /// </summary>
    public const int Attempts = 5;
    public static readonly TimeSpan Wait = TimeSpan.FromMinutes(1);

    // A new key at every start: restarting the service ends the sessions.
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset Until)> _failures = new(StringComparer.OrdinalIgnoreCase);

    public PanelUserStore Users => users;

    public TimeSpan SessionLength => TimeSpan.FromHours(Math.Max(1, options.SessionHours));

    /// <summary>
    /// True while whoever keeps guessing the password of this login has to wait.
    /// </summary>
    public bool Blocked(string? login) =>
        login is not null && _failures.TryGetValue(login.Trim(), out var state) && state.Failures >= Attempts && state.Until > time.GetUtcNow();

    /// <summary>
    /// The user that got in, or null when the login or the password is wrong.
    /// </summary>
    public PanelUser? Accepts(string? login, string? password)
    {
        if (Blocked(login))
            return null;
        var user = users.Check(login, password);
        if (login is { Length: > 0 and <= 100 })
        {
            if (user is not null)
                _failures.TryRemove(login.Trim(), out _);
            else if (_failures.Count < 10_000)
                _failures.AddOrUpdate(login.Trim(), _ => (1, time.GetUtcNow() + Wait),
                    (_, state) => (state.Until <= time.GetUtcNow() && state.Failures >= Attempts ? 1 : state.Failures + 1, time.GetUtcNow() + Wait));
        }
        return user;
    }

    /// <summary>
    /// The contents of the session cookie for a user who has just logged in.
    /// </summary>
    public string Issue(string login)
    {
        var stamp = users.Find(login)?.Stamp ?? 0;
        var payload = $"{Convert.ToBase64String(Encoding.UTF8.GetBytes(login))}.{(time.GetUtcNow() + SessionLength).ToUnixTimeSeconds()}.{stamp}";
        return $"{payload}.{Sign(payload)}";
    }

    /// <summary>
    /// The user a session cookie belongs to, or null when it is not a valid one anymore: it
    /// expired, or the user was removed, disabled or given another password since.
    /// </summary>
    public string? Validate(string? token) => Session(token)?.Login;

    public PanelUser? Session(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        var cut = token.LastIndexOf('.');
        if (cut <= 0 || !FixedTimeEquals(Sign(token[..cut]), token[(cut + 1)..]))
            return null;

        var parts = token[..cut].Split('.');
        if (parts.Length != 3 || !long.TryParse(parts[1], out var expires) || expires < time.GetUtcNow().ToUnixTimeSeconds() || !int.TryParse(parts[2], out var stamp))
            return null;

        try
        {
            return users.Find(Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]))) is { Enabled: true } user && user.Stamp == stamp ? user : null;
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
