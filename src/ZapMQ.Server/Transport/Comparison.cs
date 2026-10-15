using System.Text.Json;
using System.Text.Json.Nodes;
using ZapMQ.Server.Panel;

namespace ZapMQ.Server.Transport;

/// <summary>
/// What an environment has, in a few words: every object of its database and every application
/// on its machine, each with what tells one version of it from another and when it last changed.
/// Nothing of what is in the tables, nor the contents of the files.
/// </summary>
public sealed class Inventory
{
    public string Environment { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string? Database { get; set; }
    /// <summary>
    /// Why the objects of the database are not in it, when they are not.
    /// </summary>
    public string? DatabaseProblem { get; set; }
    public string? TargetsProblem { get; set; }
    public List<InventoryObject> Objects { get; set; } = [];
    public List<InventoryTarget> Targets { get; set; } = [];
}

public sealed class InventoryObject
{
    public string Kind { get; set; } = "";
    public string Schema { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset? ModifiedAt { get; set; }
    public string? Fingerprint { get; set; }
}

public sealed class InventoryTarget
{
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public string? Mode { get; set; }
    public int Files { get; set; }
    public long Bytes { get; set; }
    public string? Fingerprint { get; set; }
    public DateTimeOffset? Newest { get; set; }
    public string? Problem { get; set; }
}

/// <summary>
/// One thing compared: the same, different, only here or only there; and which side has the newer
/// one, when that can be told.
/// </summary>
public sealed record Compared(
    string Kind, string? ObjectKind, string? Schema, string Name, string State, string? Newer,
    DateTimeOffset? HereAt, DateTimeOffset? ThereAt, string? HereVersion, string? ThereVersion,
    int? HereFiles, int? ThereFiles, long? HereBytes, long? ThereBytes, string? Mode);

/// <summary>
/// The comparison of this environment with another one reached by a route, asked from here.
/// </summary>
public sealed class Comparison(WorkerControlClient worker, TransportSender sender, TransportStore store, TimeProvider time, ILogger<Comparison> logger)
{
    private static readonly JsonSerializerOptions Format = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// How long the other side may take: it reads every file of every application the first time.
    /// </summary>
    public static readonly TimeSpan Patience = TimeSpan.FromMinutes(5);

    private async Task<JsonNode> Ask(string command, string by, Action<JsonObject> more, CancellationToken cancellation)
    {
        var answer = await worker.AskAsync(command, by, Patience, more, cancellation);
        if (!answer.Reached)
            throw new TransportRefused(answer.Problem ?? "O Worker Control não respondeu", StatusCodes.Status503ServiceUnavailable);
        if (!answer.Ok)
            throw new TransportRefused(answer.ErrorMessage ?? "O Worker Control recusou o pedido", answer.ErrorCode == "unknown-command" ? StatusCodes.Status501NotImplemented : StatusCodes.Status409Conflict);
        return answer.Body!;
    }

    /// <summary>
    /// What this environment has. An object whose fingerprint the agent does not know yet is read
    /// now, a few hundred at most.
    /// </summary>
    public async Task<Inventory> Here(string by, CancellationToken cancellation)
    {
        var inventory = new Inventory { Environment = store.Environment, At = time.GetUtcNow() };
        try
        {
            var body = await Ask("DatabaseInventory", by, _ => { }, cancellation);
            inventory.Database = body["Database"]?.GetValue<string>();
            inventory.Objects = body["Objects"].Deserialize<List<InventoryObject>>(Format) ?? [];
            foreach (var item in inventory.Objects.Where(item => item.Fingerprint is null).Take(400))
            {
                try
                {
                    var detail = await Ask("DatabaseObject", by, request => { request["Kind"] = item.Kind; request["Schema"] = item.Schema; request["Name"] = item.Name; }, cancellation);
                    item.Fingerprint = detail["Fingerprint"]?.GetValue<string>();
                }
                catch (TransportRefused)
                {
                    // Gone, or not readable: it stays without one.
                }
            }
        }
        catch (TransportRefused refused)
        {
            inventory.DatabaseProblem = refused.Message;
        }
        try
        {
            var body = await Ask("TransportInventory", by, _ => { }, cancellation);
            inventory.Targets = body["Targets"].Deserialize<List<InventoryTarget>>(Format) ?? [];
        }
        catch (TransportRefused refused)
        {
            inventory.TargetsProblem = refused.Status == StatusCodes.Status501NotImplemented ? "O Worker Control é anterior à versão 2.28: as aplicações não são comparadas" : refused.Message;
        }
        return inventory;
    }

    public async Task<Inventory> There(string route, CancellationToken cancellation) =>
        JsonSerializer.Deserialize<Inventory>(await sender.QueryAsync(route, "/compare/inventory", new { }, Patience, cancellation), Format)
        ?? throw new TransportRefused("A resposta do outro ambiente não pôde ser lida", StatusCodes.Status502BadGateway);

    public async Task<object> Compare(string route, string by, CancellationToken cancellation)
    {
        var there = await There(route, cancellation);
        var here = await Here(by, cancellation);
        logger.LogInformation("Transport: {Here} compared with {There} (by {User})", here.Environment, there.Environment, by);

        var items = new List<Compared>();
        static string ObjectKey(InventoryObject item) => $"{item.Kind}|{item.Schema}|{item.Name}".ToLowerInvariant();
        var theirs = there.Objects.GroupBy(ObjectKey).ToDictionary(group => group.Key, group => group.First());
        var mine = here.Objects.GroupBy(ObjectKey).ToDictionary(group => group.Key, group => group.First());
        if (here.DatabaseProblem is null && there.DatabaseProblem is null)
            foreach (var key in mine.Keys.Union(theirs.Keys))
            {
                var a = mine.GetValueOrDefault(key);
                var b = theirs.GetValueOrDefault(key);
                var any = (a ?? b)!;
                var state = a is null ? "there" : b is null ? "here"
                    : a.Fingerprint is null || b.Fingerprint is null ? "unknown"
                    : a.Fingerprint == b.Fingerprint ? "same" : "different";
                items.Add(new Compared("database", any.Kind, any.Schema, any.Name, state, state is "different" or "unknown" ? Later(a!.ModifiedAt, b!.ModifiedAt) : null,
                    a?.ModifiedAt, b?.ModifiedAt, null, null, null, null, null, null, null));
            }

        static string TargetKey(InventoryTarget item) => $"{item.Kind}|{item.Name}".ToLowerInvariant();
        var theirTargets = there.Targets.GroupBy(TargetKey).ToDictionary(group => group.Key, group => group.First());
        var myTargets = here.Targets.GroupBy(TargetKey).ToDictionary(group => group.Key, group => group.First());
        if (here.TargetsProblem is null && there.TargetsProblem is null)
            foreach (var key in myTargets.Keys.Union(theirTargets.Keys))
            {
                var a = myTargets.GetValueOrDefault(key);
                var b = theirTargets.GetValueOrDefault(key);
                var any = (a ?? b)!;
                var state = a is null ? "there" : b is null ? "here"
                    : a.Fingerprint is null || b.Fingerprint is null ? "unknown"
                    : a.Fingerprint == b.Fingerprint ? "same" : "different";
                var newer = state is "different" or "unknown" ? Newer(a!.Version, b!.Version) ?? Later(a.Newest, b.Newest) : null;
                items.Add(new Compared(any.Kind, null, null, any.Name, state, newer, a?.Newest, b?.Newest, a?.Version, b?.Version, a?.Files, b?.Files, a?.Bytes, b?.Bytes, any.Mode));
            }

        return new
        {
            here = new { environment = here.Environment, at = here.At, database = here.Database, databaseProblem = here.DatabaseProblem, targetsProblem = here.TargetsProblem },
            there = new { environment = there.Environment, at = there.At, database = there.Database, databaseProblem = there.DatabaseProblem, targetsProblem = there.TargetsProblem },
            items = items.OrderBy(item => item.Kind).ThenBy(item => item.Schema).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>
    /// "here" or "there", whichever changed later; null when it cannot be told. Less than a minute apart is the same moment.
    /// </summary>
    private static string? Later(DateTimeOffset? here, DateTimeOffset? there) =>
        here is null || there is null || (here.Value - there.Value).Duration() < TimeSpan.FromMinutes(1) ? null : here > there ? "here" : "there";

    /// <summary>
    /// By the versions they say they are, when both say one that can be compared.
    /// </summary>
    private static string? Newer(string? here, string? there)
    {
        static System.Version? Parse(string? text) =>
            text is null ? null : System.Version.TryParse(new string(text.TrimStart('v', 'V').TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.'), out var parsed) ? parsed : null;
        if (Parse(here) is not { } a || Parse(there) is not { } b || a == b)
            return null;
        return a > b ? "here" : "there";
    }

    /// <summary>
    /// The script of one object on each side, to be compared line by line.
    /// </summary>
    public async Task<object> Object(string route, string? kind, string? schema, string? name, string by, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(name))
            throw new TransportRefused("Informe o tipo, o schema e o nome do objeto");
        var there = JsonNode.Parse(await sender.QueryAsync(route, "/compare/object", new { kind, schema, name }, TimeSpan.FromMinutes(1), cancellation));
        return new { here = await Script(kind, schema, name, by, cancellation), there = there?["script"]?.GetValue<string>() };
    }

    /// <summary>
    /// The script of an object of this environment; null when it is not here.
    /// </summary>
    public async Task<string?> Script(string kind, string schema, string name, string by, CancellationToken cancellation)
    {
        try
        {
            return (await Ask("DatabaseObject", by, request => { request["Kind"] = kind; request["Schema"] = schema; request["Name"] = name; }, cancellation))["Script"]?.GetValue<string>();
        }
        catch (TransportRefused)
        {
            return null;
        }
    }

    /// <summary>
    /// The files of one application on each side, each with what it is and when it was written.
    /// </summary>
    public async Task<object> Target(string route, string? kind, string? name, string by, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(name))
            throw new TransportRefused("Informe o tipo e o nome da aplicação");
        var there = JsonNode.Parse(await sender.QueryAsync(route, "/compare/target", new { kind, name }, Patience, cancellation));
        var theirs = (there?["files"] as JsonArray ?? []).Select(FileOf).ToDictionary(file => file.Path.ToLowerInvariant());
        var mine = (await Files(kind, name, by, cancellation)).ToDictionary(file => file.Path.ToLowerInvariant());
        var files = mine.Keys.Union(theirs.Keys).Select(key =>
        {
            var a = mine.GetValueOrDefault(key);
            var b = theirs.GetValueOrDefault(key);
            var state = a is null ? "there" : b is null ? "here" : a.Sha256 == b.Sha256 ? "same" : "different";
            return new { path = (a ?? b)!.Path, state, newer = state == "different" ? Later(a!.Modified, b!.Modified) : null, hereSize = a?.Size, thereSize = b?.Size, hereAt = a?.Modified, thereAt = b?.Modified };
        }).OrderBy(file => file.state == "same").ThenBy(file => file.path, StringComparer.OrdinalIgnoreCase);
        return new { files };
    }

    public sealed record TargetFile(string Path, long Size, string Sha256, DateTimeOffset? Modified);

    private static TargetFile FileOf(JsonNode? file) =>
        new(file?["path"]?.GetValue<string>() ?? file?["Path"]?.GetValue<string>() ?? "", file?["size"]?.GetValue<long>() ?? file?["Size"]?.GetValue<long>() ?? 0,
            file?["sha256"]?.GetValue<string>() ?? file?["Sha256"]?.GetValue<string>() ?? "", (file?["modified"] ?? file?["Modified"])?.GetValue<DateTimeOffset?>());

    /// <summary>
    /// The files of an application of this environment; none when it is not here.
    /// </summary>
    public async Task<List<TargetFile>> Files(string kind, string name, string by, CancellationToken cancellation)
    {
        try
        {
            var body = await Ask("TransportTarget", by, request => { request["Kind"] = kind; request["Name"] = name; }, cancellation);
            return [.. (body["Files"] as JsonArray ?? []).Select(FileOf)];
        }
        catch (TransportRefused)
        {
            return [];
        }
    }
}
