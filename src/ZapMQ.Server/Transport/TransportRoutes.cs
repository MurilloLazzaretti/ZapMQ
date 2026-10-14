using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZapMQ.Server.Transport;

/// <summary>
/// Where a package can be sent from here: a name, the address of the panel of the other
/// environment and the key that environment gave for this one.
/// </summary>
public sealed class TransportRoute
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>
    /// The key, as kept on disk: protected by the machine where it can be.
    /// </summary>
    public string Key { get; set; } = "";
    /// <summary>
    /// Plain HTTP was asked for, knowingly: an internal network without a certificate.
    /// </summary>
    public bool AllowHttp { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset? LastUsedAt { get; set; }
    public string? LastProblem { get; set; }
}

/// <summary>
/// An environment that may send packages here, and the key it signs them with.
/// </summary>
public sealed class TransportOrigin
{
    public string Name { get; set; } = "";
    public string Key { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset? LastSeenAt { get; set; }
}

/// <summary>
/// Keys kept on disk. On Windows they are protected for this machine (DPAPI): the file copied to
/// another machine says nothing. Elsewhere, where there is no such protection, they are kept as
/// they are and marked so.
/// </summary>
public static class TransportSecret
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ZapMQ.Transport.Route");

    public static string Protect(byte[] key)
    {
        if (OperatingSystem.IsWindows())
            return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(key, Entropy, DataProtectionScope.LocalMachine));
        return "plain:" + Convert.ToBase64String(key);
    }

    public static byte[]? Unprotect(string kept)
    {
        try
        {
            if (kept.StartsWith("dpapi:", StringComparison.Ordinal) && OperatingSystem.IsWindows())
                return ProtectedData.Unprotect(Convert.FromBase64String(kept[6..]), Entropy, DataProtectionScope.LocalMachine);
            if (kept.StartsWith("plain:", StringComparison.Ordinal))
                return Convert.FromBase64String(kept[6..]);
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
        }
        return null;
    }

    /// <summary>
    /// A key as it is shown once and typed on the other side: a mark of what it is, and 32 bytes.
    /// </summary>
    public static string Show(byte[] key) => "zt1." + Convert.ToBase64String(key).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[]? Read(string? shown)
    {
        if (shown is null || !shown.Trim().StartsWith("zt1.", StringComparison.Ordinal))
            return null;
        var text = shown.Trim()[4..].Replace('-', '+').Replace('_', '/');
        text += new string('=', (4 - text.Length % 4) % 4);
        try
        {
            return Convert.FromBase64String(text) is { Length: 32 } key ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// The signature of a request from one environment to another: HMAC-SHA256, with the key of the
/// route, of the method, the path below the inbound address, the moment, a value used once and the
/// SHA-256 of the body. Whoever does not have the key cannot make one, a body changed on the way
/// does not match, and a request heard and sent again is refused.
/// </summary>
public static class TransportSignature
{
    public const string OriginHeader = "X-ZapMQ-Origin";
    public const string TimeHeader = "X-ZapMQ-Time";
    public const string NonceHeader = "X-ZapMQ-Nonce";
    public const string ContentHeader = "X-ZapMQ-Content-Sha256";
    public const string SignatureHeader = "X-ZapMQ-Signature";

    /// <summary>
    /// How far apart the clocks of the two ends may be.
    /// </summary>
    public static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    public static string ContentHash(ReadOnlySpan<byte> body) => Convert.ToHexStringLower(SHA256.HashData(body));

    public static string Sign(byte[] key, string method, string path, long time, string nonce, string contentHash)
    {
        var text = $"{method.ToUpperInvariant()}\n{path}\n{time}\n{nonce}\n{contentHash}";
        return Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(text)));
    }

    public static void Apply(HttpRequestMessage request, string origin, byte[] key, string path, byte[] body, DateTimeOffset now)
    {
        var time = now.ToUnixTimeSeconds();
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var hash = ContentHash(body);
        request.Headers.Add(OriginHeader, Uri.EscapeDataString(origin));
        request.Headers.Add(TimeHeader, time.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add(NonceHeader, nonce);
        request.Headers.Add(ContentHeader, hash);
        request.Headers.Add(SignatureHeader, Sign(key, request.Method.Method, path, time, nonce, hash));
    }
}

/// <summary>
/// The routes from here and the origins that may send here, kept in <c>routes.json</c> beside the
/// packages. Only the master changes them; the keys never leave in an answer, except a new key of
/// an origin, once, to be typed on the other side.
/// </summary>
public sealed class TransportRouteStore
{
    private sealed class Kept
    {
        public List<TransportRoute> Routes { get; set; } = [];
        public List<TransportOrigin> Origins { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();
    private Kept _kept = new();

    public TransportRouteStore(string root, ILogger<TransportRouteStore> logger)
    {
        _path = Path.Combine(root, "routes.json");
        try
        {
            if (File.Exists(_path))
                _kept = JsonSerializer.Deserialize<Kept>(File.ReadAllText(_path)) ?? new Kept();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogError("The transport routes in {Path} could not be read: {Error}", _path, error.Message);
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_kept, Format));
        File.Move(temporary, _path, overwrite: true);
    }

    private static TransportRoute Copy(TransportRoute route) => new()
    {
        Id = route.Id, Name = route.Name, Url = route.Url, Key = route.Key, AllowHttp = route.AllowHttp, CreatedAt = route.CreatedAt, CreatedBy = route.CreatedBy,
        LastUsedAt = route.LastUsedAt, LastProblem = route.LastProblem
    };

    private static TransportOrigin Copy(TransportOrigin origin) => new()
    {
        Name = origin.Name, Key = origin.Key, CreatedAt = origin.CreatedAt, CreatedBy = origin.CreatedBy, LastSeenAt = origin.LastSeenAt
    };

    public IReadOnlyList<TransportRoute> Routes()
    {
        lock (_gate)
            return [.. _kept.Routes.Select(Copy)];
    }

    public IReadOnlyList<TransportOrigin> Origins()
    {
        lock (_gate)
            return [.. _kept.Origins.Select(Copy)];
    }

    public TransportRoute? Route(string id)
    {
        lock (_gate)
            return _kept.Routes.FirstOrDefault(route => route.Id == id) is { } found ? Copy(found) : null;
    }

    /// <summary>
    /// What is said of an address: refused, or the address written the way it is kept.
    /// </summary>
    public static string CheckUrl(string? url, bool allowHttp)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || !string.IsNullOrEmpty(uri.Query))
            throw new TransportRefused("O endereço deve ser o do painel do outro ambiente, como https://servidor/zapmq");
        if (uri.Scheme == Uri.UriSchemeHttp && !allowHttp)
            throw new TransportRefused("O endereço deve ser https. Para uma rede interna sem certificado, marque que aceita http");
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private static string CheckName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is 0 or > 40 || trimmed.Any(c => char.IsControl(c) || c is '/' or '\\' or '|'))
            throw new TransportRefused("Dê um nome curto, como QAS ou PRD");
        return trimmed;
    }

    public TransportRoute AddRoute(string? name, string? url, string? key, bool allowHttp, string by, DateTimeOffset now)
    {
        var clean = CheckName(name);
        var address = CheckUrl(url, allowHttp);
        var bytes = TransportSecret.Read(key) ?? throw new TransportRefused("A chave não é uma chave de rota: ela começa com zt1. e é dada pelo ambiente de destino, em Origens autorizadas");
        lock (_gate)
        {
            if (_kept.Routes.Any(route => string.Equals(route.Name, clean, StringComparison.OrdinalIgnoreCase)))
                throw new TransportRefused($"Já existe uma rota chamada {clean}", StatusCodes.Status409Conflict);
            var route = new TransportRoute
            {
                Id = Guid.NewGuid().ToString("N")[..12], Name = clean, Url = address, Key = TransportSecret.Protect(bytes), AllowHttp = allowHttp, CreatedAt = now, CreatedBy = by
            };
            _kept.Routes.Add(route);
            Save();
            return Copy(route);
        }
    }

    public TransportRoute ChangeRoute(string id, string? name, string? url, string? key, bool allowHttp)
    {
        var clean = CheckName(name);
        var address = CheckUrl(url, allowHttp);
        byte[]? bytes = null;
        if (!string.IsNullOrWhiteSpace(key))
            bytes = TransportSecret.Read(key) ?? throw new TransportRefused("A chave não é uma chave de rota: ela começa com zt1.");
        lock (_gate)
        {
            var route = _kept.Routes.FirstOrDefault(item => item.Id == id) ?? throw new TransportRefused("Não existe essa rota", StatusCodes.Status404NotFound);
            if (_kept.Routes.Any(other => other.Id != id && string.Equals(other.Name, clean, StringComparison.OrdinalIgnoreCase)))
                throw new TransportRefused($"Já existe uma rota chamada {clean}", StatusCodes.Status409Conflict);
            route.Name = clean;
            route.Url = address;
            route.AllowHttp = allowHttp;
            if (bytes is not null)
                route.Key = TransportSecret.Protect(bytes);
            Save();
            return Copy(route);
        }
    }

    public bool RemoveRoute(string id)
    {
        lock (_gate)
        {
            if (_kept.Routes.RemoveAll(route => route.Id == id) == 0)
                return false;
            Save();
            return true;
        }
    }

    public void Used(string id, DateTimeOffset now, string? problem)
    {
        lock (_gate)
        {
            if (_kept.Routes.FirstOrDefault(route => route.Id == id) is not { } route)
                return;
            route.LastUsedAt = now;
            route.LastProblem = problem;
            Save();
        }
    }

    /// <summary>
    /// Lets an environment send packages here, with a new key: the one time it is shown.
    /// </summary>
    public (TransportOrigin Origin, string Key) AddOrigin(string? name, string by, DateTimeOffset now)
    {
        var clean = CheckName(name);
        var key = RandomNumberGenerator.GetBytes(32);
        lock (_gate)
        {
            if (_kept.Origins.Any(origin => string.Equals(origin.Name, clean, StringComparison.OrdinalIgnoreCase)))
                throw new TransportRefused($"{clean} já é uma origem autorizada; para trocar a chave, exclua e autorize de novo", StatusCodes.Status409Conflict);
            var origin = new TransportOrigin { Name = clean, Key = TransportSecret.Protect(key), CreatedAt = now, CreatedBy = by };
            _kept.Origins.Add(origin);
            Save();
            return (Copy(origin), TransportSecret.Show(key));
        }
    }

    public bool RemoveOrigin(string name)
    {
        lock (_gate)
        {
            if (_kept.Origins.RemoveAll(origin => string.Equals(origin.Name, name, StringComparison.OrdinalIgnoreCase)) == 0)
                return false;
            Save();
            return true;
        }
    }

    public byte[]? OriginKey(string name)
    {
        lock (_gate)
            return _kept.Origins.FirstOrDefault(origin => string.Equals(origin.Name, name, StringComparison.OrdinalIgnoreCase)) is { } found ? TransportSecret.Unprotect(found.Key) : null;
    }

    public void Seen(string name, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_kept.Origins.FirstOrDefault(origin => string.Equals(origin.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } origin)
                return;
            // Once a minute is enough to say it is in use, without writing the file on every part.
            if (origin.LastSeenAt is { } last && now - last < TimeSpan.FromMinutes(1))
                return;
            origin.LastSeenAt = now;
            Save();
        }
    }
}

/// <summary>
/// The door through which another environment hands packages in. It is outside the login of the
/// panel, so it lets nothing through that is not signed by an authorized origin, recent, never seen
/// before and whole.
/// </summary>
public sealed class TransportInbound(TransportRouteStore routes, TransportStore store, TimeProvider time, ILogger<TransportInbound> logger)
{
    public const long MaxPart = 8L * 1024 * 1024;
    public const long MaxPackage = 512L * 1024 * 1024;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _nonces = new(StringComparer.Ordinal);

    private string Folder(string origin) => Path.Combine(store.Root, "inbound", string.Concat(origin.Select(c => char.IsLetterOrDigit(c) ? c : '_')));

    private static bool ValidId(string id) => id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>
    /// The origin that signed the request, or why it is refused.
    /// </summary>
    public (string? Origin, string? Problem) Check(HttpContext context, string path, byte[] body)
    {
        var headers = context.Request.Headers;
        var origin = Uri.UnescapeDataString(headers[TransportSignature.OriginHeader].ToString());
        var timeText = headers[TransportSignature.TimeHeader].ToString();
        var nonce = headers[TransportSignature.NonceHeader].ToString();
        var hash = headers[TransportSignature.ContentHeader].ToString();
        var signature = headers[TransportSignature.SignatureHeader].ToString();
        if (origin.Length == 0 || nonce.Length is < 16 or > 64 || signature.Length == 0 || !long.TryParse(timeText, out var sent))
            return (null, "pedido sem assinatura");
        var now = time.GetUtcNow();
        if ((now - DateTimeOffset.FromUnixTimeSeconds(sent)).Duration() > TransportSignature.Skew)
            return (null, "o relógio do outro ambiente está a mais de 5 minutos deste, ou o pedido é antigo");
        if (routes.OriginKey(origin) is not { } key)
            return (null, $"{origin} não é uma origem autorizada neste ambiente");
        if (!string.Equals(TransportSignature.ContentHash(body), hash, StringComparison.OrdinalIgnoreCase))
            return (null, "o conteúdo não é o que foi assinado");
        var expected = Convert.FromBase64String(TransportSignature.Sign(key, context.Request.Method, path, sent, nonce, hash.ToLowerInvariant()));
        byte[] given;
        try
        {
            given = Convert.FromBase64String(signature);
        }
        catch (FormatException)
        {
            return (null, "assinatura inválida");
        }
        if (!CryptographicOperations.FixedTimeEquals(expected, given))
            return (null, "assinatura inválida: a chave da rota não é a desta origem");
        // A request that was heard and sent again carries a value already used.
        if (!_nonces.TryAdd(origin + "|" + nonce, now))
            return (null, "este pedido já foi recebido uma vez");
        foreach (var (used, at) in _nonces)
        {
            if (now - at > TransportSignature.Skew * 2)
                _nonces.TryRemove(used, out _);
        }
        routes.Seen(origin, now);
        return (origin, null);
    }

    public long Received(string origin, string id) =>
        ValidId(id) && File.Exists(Path.Combine(Folder(origin), id + ".part")) ? new FileInfo(Path.Combine(Folder(origin), id + ".part")).Length : 0;

    public bool Has(string id) => store.Find(id) is not null;

    /// <summary>
    /// A part of a package, written after what already arrived. It has to start where the rest ended.
    /// </summary>
    public (bool Ok, long Received, string? Problem) Part(string origin, string id, long offset, byte[] body)
    {
        if (!ValidId(id))
            return (false, 0, "identidade de pacote inválida");
        var folder = Folder(origin);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, id + ".part");
        var have = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (offset == 0 && have > 0)
        {
            // Starting over: what was there is of an attempt that will not be finished.
            File.Delete(path);
            have = 0;
        }
        if (offset != have)
            return (false, have, $"esta parte começa em {offset}, e o que chegou até agora termina em {have}");
        if (have + body.Length > MaxPackage)
            return (false, have, "o pacote passa do tamanho máximo");
        using (var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
            file.Write(body);
        return (true, have + body.Length, null);
    }

    /// <summary>
    /// Everything arrived: it is checked against what the origin says it sent and becomes a
    /// pending package, which nobody applies without approving it here.
    /// </summary>
    public (PackageRecord? Record, bool AlreadyHere, string? Problem) Complete(string origin, string id, long size, string? sha256)
    {
        if (!ValidId(id))
            return (null, false, "identidade de pacote inválida");
        if (store.Find(id) is not null)
            return (null, true, null);
        var path = Path.Combine(Folder(origin), id + ".part");
        if (!File.Exists(path))
            return (null, false, "nenhuma parte deste pacote chegou");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != size || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            return (null, false, "o pacote chegou diferente do que foi enviado; envie de novo");
        }
        try
        {
            var record = store.Import(bytes, $"{origin} (envio)", time.GetUtcNow());
            File.Delete(path);
            logger.LogInformation("Transport: package {Name} ({Id}) received from {Origin}, {Size} bytes", record.Manifest.Name, id, origin, bytes.Length);
            return (record, false, null);
        }
        catch (TransportRefused refused)
        {
            File.Delete(path);
            return (null, false, refused.Message);
        }
    }
}

/// <summary>
/// Sends a package to another environment along one of the routes: in parts, picking up where a
/// broken attempt stopped, and done when the other side says it has the whole of it. From then on
/// it is the business of the other environment.
/// </summary>
public sealed class TransportSender(TransportRouteStore routes, TransportStore store, TimeProvider time, ILogger<TransportSender> logger)
{
    public const int PartSize = 4 * 1024 * 1024;

    public sealed class Sending
    {
        public string Package { get; set; } = "";
        public string RouteId { get; set; } = "";
        public string Route { get; set; } = "";
        public string Status { get; set; } = "sending";
        public long Sent { get; set; }
        public long Total { get; set; }
        public string? Problem { get; set; }
        public string By { get; set; } = "";
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset? EndedAt { get; set; }
    }

    private readonly ConcurrentDictionary<string, Sending> _sendings = new(StringComparer.Ordinal);

    /// <summary>
    /// Made for each request: a test replaces it to talk to a server in the same process.
    /// </summary>
    public Func<HttpClient> Client { get; set; } = () => new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

    public IReadOnlyList<Sending> Of(string package) =>
        [.. _sendings.Values.Where(item => item.Package == package).OrderByDescending(item => item.StartedAt)];

    private async Task<HttpResponseMessage> Ask(HttpClient client, TransportRoute route, byte[] key, HttpMethod method, string below, byte[] body, CancellationToken stopping)
    {
        var request = new HttpRequestMessage(method, route.Url + "/api/transport/inbound" + below);
        if (body.Length > 0 || method != HttpMethod.Get)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(below.EndsWith("/complete", StringComparison.Ordinal) || below == "/hello" ? "application/json" : "application/octet-stream");
        }
        TransportSignature.Apply(request, store.Environment, key, below, body, time.GetUtcNow());
        return await client.SendAsync(request, stopping);
    }

    private static async Task<string> Problem(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        try
        {
            if (JsonDocument.Parse(text).RootElement.TryGetProperty("error", out var error) && error.GetString() is { Length: > 0 } message)
                return message;
        }
        catch (JsonException)
        {
        }
        return response.StatusCode switch
        {
            System.Net.HttpStatusCode.NotFound => "o endereço não é o painel de um ZapMQ que recebe pacotes (versão 2.11 ou depois)",
            System.Net.HttpStatusCode.RequestEntityTooLarge => "o proxy do destino recusou o tamanho da parte (client_max_body_size)",
            _ => $"o destino respondeu {(int)response.StatusCode}"
        };
    }

    private (TransportRoute Route, byte[] Key) Resolve(string routeId)
    {
        var route = routes.Route(routeId) ?? throw new TransportRefused("Não existe essa rota", StatusCodes.Status404NotFound);
        var key = TransportSecret.Unprotect(route.Key) ?? throw new TransportRefused($"A chave da rota {route.Name} não pode ser lida nesta máquina; informe a chave de novo");
        return (route, key);
    }

    /// <summary>
    /// Asks the other side who it is, signed: proves the address, the key and the clocks.
    /// </summary>
    public async Task<string> TestAsync(string routeId, CancellationToken stopping)
    {
        var (route, key) = Resolve(routeId);
        try
        {
            using var client = Client();
            using var response = await Ask(client, route, key, HttpMethod.Post, "/hello", Encoding.UTF8.GetBytes("{}"), stopping);
            if (!response.IsSuccessStatusCode)
                throw new TransportRefused(await Problem(response), StatusCodes.Status502BadGateway);
            var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync(stopping)).RootElement;
            routes.Used(routeId, time.GetUtcNow(), null);
            return answer.TryGetProperty("environment", out var environment) ? environment.GetString() ?? "" : "";
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            var message = Describe(error);
            routes.Used(routeId, time.GetUtcNow(), message);
            throw new TransportRefused(message, StatusCodes.Status502BadGateway);
        }
    }

    private static string Describe(Exception error) => error switch
    {
        TaskCanceledException => "o destino não respondeu a tempo",
        HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException } => "o certificado do destino não é aceito por esta máquina",
        HttpRequestException http => "não foi possível falar com o destino: " + http.Message,
        _ => error.Message
    };

    /// <summary>
    /// Starts sending, and answers at once: the panel follows the progress.
    /// </summary>
    public Sending Start(string packageId, string routeId, string by)
    {
        var record = store.Find(packageId) ?? throw new TransportRefused("Não existe esse pacote neste ambiente", StatusCodes.Status404NotFound);
        if (record.Received ? record.Status is not ("Applied") : record.Status is not ("Closed" or "Applied"))
            throw new TransportRefused(record.Received
                ? "Um pacote que chegou de outro ambiente só segue adiante depois de aplicado aqui"
                : "Só um pacote fechado (ou já aplicado) pode ser enviado");
        var (route, key) = Resolve(routeId);
        var path = store.FilePath(packageId);
        if (!File.Exists(path))
            throw new TransportRefused("O arquivo do pacote não está mais neste ambiente");
        var sending = new Sending
        {
            Package = packageId, RouteId = route.Id, Route = route.Name, Total = new FileInfo(path).Length, By = by, StartedAt = time.GetUtcNow()
        };
        var slot = packageId + "|" + route.Id;
        if (_sendings.TryGetValue(slot, out var running) && running.Status == "sending")
            throw new TransportRefused($"Este pacote já está sendo enviado para {route.Name}", StatusCodes.Status409Conflict);
        _sendings[slot] = sending;
        _ = Task.Run(() => Run(sending, route, key, path, record.Sha256));
        return sending;
    }

    private async Task Run(Sending sending, TransportRoute route, byte[] key, string path, string sha256)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            using var client = Client();
            // Where a former attempt stopped.
            long offset;
            using (var asked = await Ask(client, route, key, HttpMethod.Get, $"/packages/{sending.Package}", [], CancellationToken.None))
            {
                if (!asked.IsSuccessStatusCode)
                    throw new TransportRefused(await Problem(asked));
                var state = JsonDocument.Parse(await asked.Content.ReadAsStringAsync()).RootElement;
                offset = state.GetProperty("received").GetInt64();
                if (state.GetProperty("here").GetBoolean())
                    offset = bytes.Length;
                else if (offset > bytes.Length)
                    offset = 0;
            }
            sending.Sent = offset;
            while (offset < bytes.Length)
            {
                var part = bytes.AsSpan((int)offset, (int)Math.Min(PartSize, bytes.Length - offset)).ToArray();
                using var response = await Ask(client, route, key, HttpMethod.Put, $"/packages/{sending.Package}/parts/{offset}", part, CancellationToken.None);
                if (!response.IsSuccessStatusCode)
                    throw new TransportRefused(await Problem(response));
                offset += part.Length;
                sending.Sent = offset;
            }
            var done = JsonSerializer.SerializeToUtf8Bytes(new { size = (long)bytes.Length, sha256 });
            using (var finished = await Ask(client, route, key, HttpMethod.Post, $"/packages/{sending.Package}/complete", done, CancellationToken.None))
            {
                if (!finished.IsSuccessStatusCode)
                    throw new TransportRefused(await Problem(finished));
            }
            var now = time.GetUtcNow();
            store.Delivered(sending.Package, route.Name, sending.By, now);
            routes.Used(route.Id, now, null);
            sending.Status = "delivered";
            sending.EndedAt = now;
            logger.LogInformation("Transport: package {Id} delivered to {Route} at {Url} (by {User})", sending.Package, route.Name, route.Url, sending.By);
        }
        // It runs on its own: whatever goes wrong has to end it with a result, or it would be sending forever.
        catch (Exception error)
        {
            var message = error is TransportRefused ? error.Message : Describe(error);
            sending.Status = "failed";
            sending.Problem = message;
            sending.EndedAt = time.GetUtcNow();
            routes.Used(route.Id, sending.EndedAt.Value, message);
            store.SendFailed(sending.Package, route.Name, sending.By, message, sending.EndedAt.Value);
            logger.LogWarning("Transport: package {Id} could not be delivered to {Route}: {Problem}", sending.Package, route.Name, message);
        }
    }
}
