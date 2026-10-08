using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZapMQ.Server.Panel;

/// <summary>
/// Someone who may get into the panel. The password is never kept, only what tells whether
/// one that is typed is the right one.
/// </summary>
public sealed class PanelUser
{
    public string Login { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>
    /// The one who creates and removes the others.
    /// </summary>
    public bool Master { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The password is still the one the user was given and has not chosen.
    /// </summary>
    public bool InitialPassword { get; set; }
    public string Salt { get; set; } = "";
    public string Hash { get; set; } = "";
    public int Iterations { get; set; }

    /// <summary>
    /// Changes with the password and with being disabled: a session from before is no longer good.
    /// </summary>
    public int Stamp { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
}

/// <summary>
/// Why something asked of the users was not done, in words for whoever asked.
/// </summary>
public sealed class UserRefused(string message, bool missing = false) : Exception(message)
{
    public bool Missing { get; } = missing;
}

/// <summary>
/// The users of the panel, kept in <c>users.json</c> next to the executable. The first time
/// there is none, the master is made from the user and password of the settings; from then on
/// the settings are not looked at again.
/// </summary>
public sealed class PanelUserStore
{
    public const int MinimumPassword = 8;
    private const int Rounds = 210_000;

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly string _path;
    private readonly Dictionary<string, PanelUser> _users = new(StringComparer.OrdinalIgnoreCase);

    public PanelUserStore(string path, PanelOptions options, TimeProvider time, ILogger<PanelUserStore> logger)
    {
        _path = path;
        _time = time;
        _logger = logger;
        Load();
        if (_users.Count > 0)
            return;

        var master = new PanelUser
        {
            Login = options.User.Trim(), Name = "Master", Master = true, CreatedAt = time.GetUtcNow(),
            // Only a password nobody chose asks to be changed.
            InitialPassword = options.HasDefaultPassword
        };
        SetPassword(master, options.Password);
        _users[master.Login] = master;
        Save();
        logger.LogInformation("The users of the panel start with the master {User}, from the settings", master.Login);
    }

    private string FileName => Path.GetFileName(_path);

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;
            foreach (var user in JsonSerializer.Deserialize<List<PanelUser>>(File.ReadAllText(_path)) ?? [])
                if (!string.IsNullOrWhiteSpace(user.Login))
                    _users[user.Login] = user;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogError("{File} could not be read; the panel starts with the master of the settings alone: {Error}", FileName, error.Message);
            _users.Clear();
        }
    }

    private void Save()
    {
        try
        {
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_users.Values.OrderByDescending(user => user.Master).ThenBy(user => user.Login, StringComparer.OrdinalIgnoreCase), Format));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("{File} could not be written; the change to the users is in force but will be lost on restart: {Error}", FileName, error.Message);
        }
    }

    private static byte[] Derive(string password, byte[] salt, int rounds) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, rounds, HashAlgorithmName.SHA256, 32);

    private static void SetPassword(PanelUser user, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        user.Salt = Convert.ToBase64String(salt);
        user.Iterations = Rounds;
        user.Hash = Convert.ToBase64String(Derive(password, salt, Rounds));
        user.Stamp++;
    }

    private static bool Matches(PanelUser user, string password)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Derive(password, Convert.FromBase64String(user.Salt), Math.Max(1, user.Iterations)), Convert.FromBase64String(user.Hash));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static PanelUser Copy(PanelUser user) => (PanelUser)JsonSerializer.Deserialize(JsonSerializer.Serialize(user), typeof(PanelUser))!;

    /// <summary>
    /// The user, when the login and the password are right and it may get in.
    /// </summary>
    public PanelUser? Check(string? login, string? password, bool remember = true)
    {
        if (string.IsNullOrWhiteSpace(login) || password is null)
            return null;
        lock (_gate)
        {
            if (!_users.TryGetValue(login.Trim(), out var user))
            {
                // The same work as for a user that exists, so that the time taken tells nothing.
                Derive(password, new byte[16], Rounds);
                return null;
            }
            if (!Matches(user, password) || !user.Enabled)
                return null;
            if (remember)
            {
                user.LastLoginAt = _time.GetUtcNow();
                Save();
            }
            return Copy(user);
        }
    }

    public PanelUser? Find(string? login)
    {
        if (string.IsNullOrWhiteSpace(login))
            return null;
        lock (_gate)
            return _users.TryGetValue(login.Trim(), out var user) ? Copy(user) : null;
    }

    public IReadOnlyList<PanelUser> List()
    {
        lock (_gate)
            return [.. _users.Values.OrderByDescending(user => user.Master).ThenBy(user => user.Login, StringComparer.OrdinalIgnoreCase).Select(Copy)];
    }

    private static void Acceptable(string? password)
    {
        if (password is null || password.Length < MinimumPassword)
            throw new UserRefused($"A senha precisa ter pelo menos {MinimumPassword} caracteres");
    }

    public PanelUser Create(string? login, string? name, string? password, string by)
    {
        login = login?.Trim() ?? "";
        if (login.Length is < 3 or > 40 || login.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '@')))
            throw new UserRefused("O login precisa ter de 3 a 40 caracteres: letras, números, ponto, traço, sublinhado ou arroba");
        Acceptable(password);
        lock (_gate)
        {
            if (_users.ContainsKey(login))
                throw new UserRefused($"Já existe um usuário {login}");
            var user = new PanelUser { Login = login, Name = string.IsNullOrWhiteSpace(name) ? login : name.Trim(), CreatedAt = _time.GetUtcNow(), CreatedBy = by, InitialPassword = true };
            SetPassword(user, password!);
            _users[login] = user;
            Save();
            return Copy(user);
        }
    }

    private PanelUser Existing(string login) =>
        _users.TryGetValue(login.Trim(), out var user) ? user : throw new UserRefused($"Não existe um usuário {login}", missing: true);

    /// <summary>
    /// Changes the name of a user or whether it may get in. The master always may.
    /// </summary>
    public PanelUser Change(string login, string? name, bool? enabled)
    {
        lock (_gate)
        {
            var user = Existing(login);
            if (enabled == false && user.Master)
                throw new UserRefused("O usuário master não pode ser desativado");
            if (!string.IsNullOrWhiteSpace(name))
                user.Name = name.Trim();
            if (enabled is { } value && value != user.Enabled)
            {
                user.Enabled = value;
                user.Stamp++;
            }
            Save();
            return Copy(user);
        }
    }

    /// <summary>
    /// A new password given by the master: the user is expected to choose its own afterwards.
    /// </summary>
    public void Reset(string login, string? password)
    {
        Acceptable(password);
        lock (_gate)
        {
            var user = Existing(login);
            SetPassword(user, password!);
            user.InitialPassword = true;
            Save();
        }
    }

    /// <summary>
    /// The user choosing its own password, which asks for the one in use.
    /// </summary>
    public void ChangeOwn(string login, string? current, string? password)
    {
        Acceptable(password);
        lock (_gate)
        {
            var user = Existing(login);
            if (current is null || !Matches(user, current))
                throw new UserRefused("A senha atual não confere");
            if (current == password)
                throw new UserRefused("A senha nova precisa ser diferente da atual");
            SetPassword(user, password!);
            user.InitialPassword = false;
            Save();
        }
    }

    public void Delete(string login)
    {
        lock (_gate)
        {
            var user = Existing(login);
            if (user.Master)
                throw new UserRefused("O usuário master não pode ser excluído");
            _users.Remove(user.Login);
            Save();
        }
    }
}
