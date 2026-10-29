using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ZapMQ.Server.Panel;

namespace ZapMQ.Server.Transport;

/// <summary>
/// One endpoint of an API that uses an object of the database, as its map says.
/// </summary>
public sealed record EndpointUse(string Api, string Method, string Route);

/// <summary>
/// The endpoints of the APIs of this environment, as each one says it: the OpenAPI document it
/// serves and the map of what each endpoint runs in the database (docs/ENDPOINTS.md). The objects
/// of each command are read from its SQL and found in the catalog of the database; the other way
/// round, which endpoints use an object. What the Worker Control answers is kept for a while.
/// </summary>
public sealed partial class ApiEndpoints(WorkerControlClient worker, TimeProvider time)
{
    public static readonly TimeSpan Kept = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(45);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, JsonObject Answer)> _described = new(StringComparer.OrdinalIgnoreCase);
    private (string Stamp, Dictionary<string, List<EndpointUse>> Index)? _uses;
    private (DateTimeOffset At, string? Database, Dictionary<string, string> Kinds)? _catalog;

    private async Task<JsonNode> Ask(string command, string by, Action<JsonObject> more, CancellationToken cancellation)
    {
        var answer = await worker.AskAsync(command, by, Patience, more, cancellation);
        if (!answer.Reached)
            throw new TransportRefused(answer.Problem ?? "O Worker Control não respondeu", StatusCodes.Status503ServiceUnavailable);
        if (!answer.Ok)
            throw new TransportRefused(answer.ErrorMessage ?? "O Worker Control recusou o pedido", answer.ErrorCode switch
            {
                "unknown-command" => StatusCodes.Status501NotImplemented,
                "not-found" => StatusCodes.Status404NotFound,
                _ => StatusCodes.Status409Conflict
            });
        return answer.Body!;
    }

    /// <summary>
    /// What one application says about its endpoints, with the objects of each command found in
    /// the catalog of the database.
    /// </summary>
    public async Task<JsonObject> Describe(string kind, string name, bool fresh, string by, CancellationToken cancellation)
    {
        var key = $"{kind}:{name}";
        if (!fresh && _described.TryGetValue(key, out var kept) && time.GetUtcNow() - kept.At < Kept)
            return (JsonObject)kept.Answer.DeepClone();

        var answer = (JsonObject)(await Ask("ApiDocs", by, request => { request["Kind"] = kind; request["Name"] = name; }, cancellation)).DeepClone();
        var (database, kinds) = await Catalog(fresh, by, cancellation);
        answer["Database"] = database;
        if (answer["Map"] is JsonObject map && map["endpoints"] is JsonArray endpoints)
            foreach (var endpoint in endpoints.OfType<JsonObject>())
                endpoint["found"] = new JsonArray([.. Objects(endpoint).Select(found => (JsonNode)new JsonObject
                {
                    ["name"] = found,
                    ["kind"] = kinds.GetValueOrDefault(found)
                })]);
        answer["At"] = time.GetUtcNow();
        _described[key] = (time.GetUtcNow(), answer);
        return (JsonObject)answer.DeepClone();
    }

    /// <summary>
    /// The objects an endpoint uses: those its map names, and those read from the SQL of its
    /// commands. What is marked as varying (<c>/*…*/</c>) is a comment, and does not stop the reading.
    /// </summary>
    public static List<string> Objects(JsonObject endpoint)
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var named in (endpoint["objects"] as JsonArray ?? []).Select(item => item?.GetValue<string>()).Where(item => !string.IsNullOrWhiteSpace(item)))
            found.Add(Qualified(named!));
        foreach (var sql in (endpoint["commands"] as JsonArray ?? []).OfType<JsonObject>().Select(command => command["sql"]?.GetValue<string>()).Where(sql => !string.IsNullOrWhiteSpace(sql)))
        {
            var read = ScriptNames.Read(sql!);
            if (read.Problems.Count == 0)
                found.UnionWith(read.Needed);
            else
                // A text made of parts may not be whole T-SQL: what is called by name still is.
                foreach (Match match in Called().Matches(sql!))
                    found.Add(Qualified(match.Groups["name"].Value));
        }
        return [.. found];
    }

    private static string Qualified(string name)
    {
        var parts = name.Replace("[", "").Replace("]", "").Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 2 ? $"{parts[^2]}.{parts[^1]}" : $"dbo.{parts[0]}";
    }

    /// <summary>
    /// A procedure run, or a function or table read from, in a text that is not whole T-SQL.
    /// </summary>
    [GeneratedRegex(@"(?:\bEXEC(?:UTE)?\s+(?:@\w+\s*=\s*)?|\b(?:FROM|JOIN|APPLY|INTO|UPDATE)\s+)(?<name>(?:\[?\w+\]?\.)?\[?[A-Za-z_]\w*\]?)", RegexOptions.IgnoreCase)]
    private static partial Regex Called();

    /// <summary>
    /// The kind of each object of the database, by schema.name, to open it on its own screen.
    /// </summary>
    private async Task<(string? Database, Dictionary<string, string> Kinds)> Catalog(bool fresh, string by, CancellationToken cancellation)
    {
        if (!fresh && _catalog is { } kept && time.GetUtcNow() - kept.At < Kept)
            return (kept.Database, kept.Kinds);
        var kinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? database = null;
        try
        {
            var body = await Ask("DatabaseInventory", by, _ => { }, cancellation);
            database = body["Database"]?.GetValue<string>();
            foreach (var item in body["Objects"].Deserialize<List<InventoryObject>>(Web) ?? [])
                kinds.TryAdd($"{item.Schema}.{item.Name}", item.Kind);
        }
        catch (TransportRefused)
        {
            // No database is followed here: the objects are named, and not opened.
        }
        _catalog = (time.GetUtcNow(), database, kinds);
        return (database, kinds);
    }

    /// <summary>
    /// The endpoints of every API of this environment that use an object, by schema.name.
    /// </summary>
    public async Task<List<EndpointUse>> UsedBy(string schema, string name, string by, CancellationToken cancellation) =>
        (await Uses(by, cancellation)).GetValueOrDefault($"{schema}.{name}") ?? [];

    /// <summary>
    /// The index of every map, made again whenever a map changes: the maps are asked for each
    /// time (they are small files), and what is read from their SQL is kept while they stay the same.
    /// </summary>
    public async Task<Dictionary<string, List<EndpointUse>>> Uses(string by, CancellationToken cancellation)
    {
        var body = await Ask("ApiMaps", by, _ => { }, cancellation);
        var applications = (body["Applications"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var stamp = string.Join("|", applications.Select(application => $"{application["Name"]}@{application["MapFile"]}@{application["MapBuiltAt"]}@{application["Map"]?["endpoints"]?.AsArray().Count}"));
        if (_uses is { } kept && kept.Stamp == stamp)
            return kept.Index;
        var index = new Dictionary<string, List<EndpointUse>>(StringComparer.OrdinalIgnoreCase);
        foreach (var application in applications)
        {
            var api = application["Name"]?.GetValue<string>() ?? "";
            foreach (var endpoint in (application["Map"]?["endpoints"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var use = new EndpointUse(api, endpoint["method"]?.GetValue<string>()?.ToUpperInvariant() ?? "", endpoint["route"]?.GetValue<string>() ?? "");
                foreach (var found in Objects(endpoint))
                {
                    if (!index.TryGetValue(found, out var list))
                        index[found] = list = [];
                    if (!list.Contains(use))
                        list.Add(use);
                }
            }
        }
        _uses = (stamp, index);
        return index;
    }

    /// <summary>
    /// Something changed what an application says: it is asked again next time.
    /// </summary>
    public void Forget(string kind, string name)
    {
        _described.TryRemove($"{kind}:{name}", out _);
        _uses = null;
    }
}
