using System.Text.Json.Nodes;
using ZapMQ.Server.Panel;

namespace ZapMQ.Server.Transport;

/// <summary>
/// How one item of a package stands against what this environment has.
/// </summary>
public sealed record ItemCheck(int Number, string State, string? CurrentFingerprint, IReadOnlyList<string> Missing, string? Problem)
{
    /// <summary>
    /// For what is made of files: how many are new, change and go away.
    /// </summary>
    public int Added { get; init; }
    public int Changed { get; init; }
    public int Removed { get; init; }
}

/// <summary>
/// What putting back one applied item would take. State: can, changed (it can, but what is
/// there is not what the package left any more), or cannot. How: files, previous, drop or recreate.
/// </summary>
public sealed record RevertStep(int Number, string State, string? How, string? Reason);

/// <summary>
/// What an item does to one file of its target: added, changed, removed or the same.
/// </summary>
public sealed record FileChange(string Path, string State, long Size);

/// <summary>
/// What the transport does with the help of the Worker Control, which is the one that reads
/// and changes the machine: taking the script of an object when a package is closed, comparing
/// a package that arrived with what is here, and applying what was approved.
/// </summary>
public sealed class TransportService(TransportStore store, WorkerControlClient worker, TimeProvider time, ILogger<TransportService> logger)
{
    /// <summary>
    /// For how long an answer to "apply this" is waited for. A script may take minutes.
    /// </summary>
    public static readonly TimeSpan ApplyPatience = TimeSpan.FromMinutes(6);

    private static readonly string[] Order = ["Type", "Table", "Script", "Function", "View", "Procedure"];

    private sealed record Captured(string Script, string Fingerprint, string Variety, List<ItemReference> Uses);

    private static string Key(string? kind, string? schema, string? name) => $"{kind}|{schema}|{name}".ToLowerInvariant();

    private async Task<JsonNode> Ask(string command, string by, Action<JsonObject> more, CancellationToken cancellation)
    {
        var answer = await worker.AskAsync(command, by, more, cancellation);
        if (!answer.Reached)
            throw new TransportRefused(answer.Problem ?? "O Worker Control não respondeu", StatusCodes.Status503ServiceUnavailable);
        if (!answer.Ok)
            throw answer.ErrorCode == "unknown-database"
                ? new TransportRefused("Este ambiente não acompanha o banco que o item nomeia. Confira \"Databases\" na configuração do Worker Control.", StatusCodes.Status502BadGateway)
                : new TransportRefused(answer.ErrorMessage ?? "O Worker Control recusou o pedido",
                    answer.ErrorCode == "not-found" ? StatusCodes.Status404NotFound : answer.ErrorCode == "unknown-command" ? StatusCodes.Status501NotImplemented : StatusCodes.Status502BadGateway);
        return answer.Body!;
    }

    /// <summary>
    /// The object as it is right now in the database of this environment. Null when it is not there.
    /// </summary>
    private async Task<Captured?> Read(string? database, string? kind, string? schema, string? name, string by, CancellationToken cancellation)
    {
        try
        {
            var body = await Ask("DatabaseObject", by, request =>
            {
                if (!string.IsNullOrEmpty(database))
                    request["Database"] = database;
                request["Kind"] = kind;
                request["Schema"] = schema;
                request["Name"] = name;
            }, cancellation);
            if (body["Script"]?.GetValue<string>() is not { } script)
                throw new TransportRefused($"O banco guarda {schema}.{name} criptografado: não há script para transportar");
            var uses = (body["Uses"] as JsonArray ?? []).Select(item => new ItemReference
            {
                Schema = item?["Schema"]?.GetValue<string>(), Name = item?["Name"]?.GetValue<string>() ?? "", Kind = item?["Kind"]?.GetValue<string>(), Database = item?["Database"]?.GetValue<string>()
            }).ToList();
            return new Captured(script, body["Fingerprint"]?.GetValue<string>() ?? "", body["Object"]?["Variety"]?.GetValue<string>() ?? "", uses);
        }
        catch (TransportRefused refused) when (refused.Status == StatusCodes.Status404NotFound)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- the area

    /// <summary>
    /// Puts an object of the database in the area, to be carried or to be dropped where the
    /// package goes. One that is to be carried has to exist.
    /// </summary>
    public async Task<AreaItem> AddObject(string? database, string? kind, string? schema, string? name, bool drop, string by, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(name))
            throw new TransportRefused("Informe o tipo, o schema e o nome do objeto");
        database = await Logical(database, by, cancellation);
        var found = await Read(database, kind, schema, name, by, cancellation);
        if (!drop && found is null)
            throw new TransportRefused($"Não existe {schema}.{name} neste banco", StatusCodes.Status404NotFound);
        if (drop && found is not null)
            throw new TransportRefused($"{schema}.{name} ainda existe neste banco: só o que foi apagado aqui vai como exclusão");
        return store.Add(new AreaItem
        {
            Action = drop ? "Drop" : "Define", ObjectKind = kind, Variety = found?.Variety, Schema = schema, Name = name, Database = string.IsNullOrEmpty(database) ? null : database,
            Fingerprint = found?.Fingerprint, AddedBy = by, AddedAt = time.GetUtcNow()
        });
    }

    /// <summary>
    /// The database as a package may name it. Where the environment has only one, none is
    /// named: how it is called here means nothing where the package is going.
    /// </summary>
    private async Task<string?> Logical(string? database, string by, CancellationToken cancellation)
    {
        if (string.IsNullOrEmpty(database))
            return null;
        var state = await Ask("Database", by, _ => { }, cancellation);
        return (state["Databases"] as JsonArray)?.Count > 1 ? database : null;
    }

    public AreaItem AddScript(string? database, string? title, string? script, string by)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new TransportRefused("Dê um título ao script: é como ele aparece para quem aprova");
        if (string.IsNullOrWhiteSpace(script))
            throw new TransportRefused("O script está vazio");
        return store.Add(new AreaItem { Action = "Script", Title = title.Trim(), Script = script, Database = string.IsNullOrEmpty(database) ? null : database, AddedBy = by, AddedAt = time.GetUtcNow() });
    }

    // ---------------------------------------------------------------- what is made of files

    /// <summary>
    /// What is never carried, whatever a zip that was brought has in it. The Worker Control
    /// keeps the same list, and keeps it again when it puts the files in place.
    /// </summary>
    private static readonly string[] NeverCarried = ["appsettings*.json", "web.config", "ConfigWorkers.json", "*.ini", "environment.js", "env.json", "*.db", "*.db-wal", "*.db-shm", "logs/"];

    public static readonly string[] FileKinds = ["worker", "service", "api", "frontend"];

    private static bool Kept(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        foreach (var pattern in NeverCarried)
        {
            if (pattern.EndsWith('/'))
            {
                if (path.StartsWith(pattern, StringComparison.OrdinalIgnoreCase) || path.Contains("/" + pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (System.Text.RegularExpressions.Regex.IsMatch(name, "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// What tells one set of files from another, whatever way they were packed.
    /// </summary>
    private static string Fingerprint(IEnumerable<FileReference> files) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            string.Join("\n", files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).Select(file => $"{file.Path.ToLowerInvariant()}:{file.Sha256}")))));

    /// <summary>
    /// A zip of a published folder, packed again the way a package carries it: the files at
    /// the root, without what belongs to an environment. Gives what it carries.
    /// </summary>
    private static (byte[] Zip, List<FileReference> Files) Normalize(byte[] brought)
    {
        var files = new List<FileReference>();
        using var memory = new MemoryStream();
        try
        {
            using var source = new System.IO.Compression.ZipArchive(new MemoryStream(brought), System.IO.Compression.ZipArchiveMode.Read);
            var entries = source.Entries.Where(entry => !entry.FullName.EndsWith('/') && !entry.FullName.EndsWith('\\')).ToList();
            var paths = entries.Select(entry => entry.FullName.Replace('\\', '/')).ToList();
            if (paths.Any(path => path.StartsWith('/') || path.Contains(':') || path.Split('/').Any(part => part is ".." or ".")))
                throw new TransportRefused("O zip tem um arquivo que cairia fora da pasta de destino");
            // Zipped with the folder itself on the outside: the folder is taken off.
            var outer = paths.Count > 0 && paths.All(path => path.Contains('/')) && paths.Select(path => path[..path.IndexOf('/')]).Distinct().Count() == 1 ? paths[0][..(paths[0].IndexOf('/') + 1)] : "";

            using (var target = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (entry, full) in entries.Zip(paths).OrderBy(pair => pair.Second, StringComparer.OrdinalIgnoreCase))
                {
                    var path = full[outer.Length..];
                    if (path.Length == 0 || Kept(path))
                        continue;
                    using var content = new MemoryStream();
                    using (var stream = entry.Open())
                        stream.CopyTo(content);
                    var bytes = content.ToArray();
                    var copy = target.CreateEntry(path, System.IO.Compression.CompressionLevel.Optimal);
                    copy.LastWriteTime = entry.LastWriteTime;
                    using (var stream = copy.Open())
                        stream.Write(bytes);
                    files.Add(new FileReference { Path = path, Size = bytes.Length, Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) });
                }
        }
        catch (InvalidDataException)
        {
            throw new TransportRefused("O arquivo enviado não é um zip que se possa ler");
        }
        if (files.Count == 0)
            throw new TransportRefused("O zip não tem nenhum arquivo para levar");
        return (memory.ToArray(), files);
    }

    private static void RequireFileKind(string? kind, string? name)
    {
        if (kind is null || !FileKinds.Contains(kind))
            throw new TransportRefused("O tipo precisa ser worker, service, api ou frontend");
        if (string.IsNullOrWhiteSpace(name))
            throw new TransportRefused("Informe o nome do alvo");
    }

    /// <summary>
    /// What can be replaced on the machine of this environment, as the Worker Control tells it.
    /// </summary>
    public async Task<JsonNode> Targets(string by, CancellationToken cancellation) => await Ask("TransportTargets", by, _ => { }, cancellation);

    /// <summary>
    /// Puts in the area what a target is running right now, packed by the Worker Control.
    /// </summary>
    public async Task<AreaItem> AddRunning(string? kind, string? name, bool incoming, string by, CancellationToken cancellation)
    {
        RequireFileKind(kind, name);
        // Either what the target is running, or the new version somebody left for it in the inbox of the machine.
        var body = await Ask("TransportCapture", by, request =>
        {
            request["Kind"] = kind;
            request["Name"] = name;
            if (incoming)
                request["Incoming"] = true;
        }, cancellation);
        // Left by the Worker Control where the panel, on the same machine, picks it up.
        var file = body["File"]?.GetValue<string>();
        if (file is null || !File.Exists(file))
            throw new TransportRefused("O Worker Control empacotou os arquivos, mas o painel não os alcança: os dois precisam estar na mesma máquina", StatusCodes.Status502BadGateway);
        try
        {
            var (zip, files) = Normalize(await File.ReadAllBytesAsync(file, cancellation));
            return Place(kind!, body["Target"]?["Name"]?.GetValue<string>() ?? name!, body["Target"]?["Version"]?.GetValue<string>(), zip, files, by);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// Puts in the area the zip of a published folder somebody brought.
    /// </summary>
    public AreaItem AddUpload(string? kind, string? name, string? version, byte[] brought, string by)
    {
        RequireFileKind(kind, name);
        var (zip, files) = Normalize(brought);
        return Place(kind!, name!.Trim(), string.IsNullOrWhiteSpace(version) ? null : version.Trim(), zip, files, by);
    }

    private AreaItem Place(string kind, string name, string? version, byte[] zip, List<FileReference> files, string by) =>
        store.Add(new AreaItem
        {
            Kind = kind, Action = "Replace", Name = name, Version = version, Files = files, Size = zip.Length, Fingerprint = Fingerprint(files), AddedBy = by, AddedAt = time.GetUtcNow()
        }, zip);

    /// <summary>
    /// The files a target has here beside the ones an item brings: what is new, what changes
    /// and what goes away. Null when the target is not on this machine.
    /// </summary>
    private async Task<(JsonNode Target, List<FileChange> Changes)?> AgainstTarget(PackageItem item, string by, CancellationToken cancellation)
    {
        JsonNode body;
        try
        {
            body = await Ask("TransportTarget", by, request =>
            {
                request["Kind"] = item.Kind;
                request["Name"] = item.Name;
            }, cancellation);
        }
        catch (TransportRefused refused) when (refused.Status == StatusCodes.Status404NotFound)
        {
            return null;
        }
        var here = (body["Files"] as JsonArray ?? []).ToDictionary(file => file!["Path"]!.GetValue<string>(), file => file!["Sha256"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase);
        var changes = new List<FileChange>();
        foreach (var file in item.Files)
            changes.Add(new FileChange(file.Path, !here.TryGetValue(file.Path, out var has) ? "added" : has == file.Sha256 ? "same" : "changed", file.Size));
        var brought = item.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        changes.AddRange(here.Keys.Where(path => !brought.Contains(path)).Select(path => new FileChange(path, "removed", 0)));
        return (body["Target"]!, [.. changes.OrderBy(change => change.Path, StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>
    /// What an item of files does to its target here, file by file.
    /// </summary>
    public async Task<(JsonNode? Target, List<FileChange> Changes)> CompareFiles(PackageItem item, string by, CancellationToken cancellation) =>
        await AgainstTarget(item, by, cancellation) is { } found ? (found.Target, found.Changes) : (null, []);

    // ---------------------------------------------------------------- closing

    /// <summary>
    /// Closes a package with the items of the area that were chosen. The script of each object
    /// is taken now. Unless the order is said to be the one given, it follows what depends on what.
    /// </summary>
    public async Task<PackageRecord> Close(string? name, string? description, IReadOnlyList<string>? ids, bool keepOrder, string by, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new TransportRefused("Dê um nome ao pacote");
        var area = store.Area();
        var chosen = ids is { Count: > 0 } ? ids.Select(id => area.FirstOrDefault(item => item.Id == id) ?? throw new TransportRefused("Um dos itens escolhidos não está mais na expedição")).ToList() : area;
        if (chosen.Count == 0)
            throw new TransportRefused("A expedição está vazia: não há o que fechar");

        var now = time.GetUtcNow();
        var packaged = LastPackaged();
        var items = new List<(AreaItem Area, PackageItem Item, byte[] Content)>();
        foreach (var area_ in chosen)
        {
            var item = new PackageItem
            {
                Kind = area_.Kind, Action = area_.Action, ObjectKind = area_.ObjectKind, Variety = area_.Variety, Schema = area_.Schema, Name = area_.Name, Title = area_.Title, Database = area_.Database
            };
            if (area_.Kind != "database")
            {
                // What is made of files goes as it was packed when it came into the area.
                if (!File.Exists(store.AreaFile(area_.Id)))
                    throw new TransportRefused($"Os arquivos de {area_.Name} não estão mais na expedição. Tire o item e inclua de novo.");
                item.Action = "Replace";
                item.Fingerprint = area_.Fingerprint;
                item.Version = area_.Version;
                item.Files = area_.Files;
                items.Add((area_, item, await File.ReadAllBytesAsync(store.AreaFile(area_.Id), cancellation)));
                continue;
            }

            string script;
            if (area_.Action == "Script")
                script = area_.Script ?? "";
            else if (area_.Action == "Drop")
                script = $"-- {area_.ObjectKind} {area_.Schema}.{area_.Name}: apagado em {store.Environment}\n";
            else
            {
                var found = await Read(area_.Database, area_.ObjectKind, area_.Schema, area_.Name, by, cancellation)
                    ?? throw new TransportRefused($"{area_.Schema}.{area_.Name} não existe mais neste banco. Tire-o da expedição, ou inclua-o como exclusão.");
                script = found.Script;
                item.Fingerprint = found.Fingerprint;
                item.Variety = found.Variety;
                item.Uses = found.Uses;
                item.Base = await Base(area_, packaged.GetValueOrDefault(Key(area_.ObjectKind, area_.Schema, area_.Name)), by, cancellation);
            }
            items.Add((area_, item, System.Text.Encoding.UTF8.GetBytes(script)));
        }

        if (!keepOrder)
            items = Arrange(items);
        for (var index = 0; index < items.Count; index++)
            items[index].Item.Number = index + 1;

        var manifest = new PackageManifest
        {
            Id = Guid.NewGuid().ToString("N"), Name = name.Trim(), Description = description?.Trim() ?? "", Origin = store.Environment, CreatedAt = now, CreatedBy = by,
            Items = [.. items.Select(item => item.Item)]
        };
        var record = store.Close(manifest, [.. items.Select(item => item.Content)], chosen.Select(item => item.Id), by, now);
        logger.LogInformation("Transport: package {Name} ({Id}) closed with {Items} items (by {User})", manifest.Name, manifest.Id, manifest.Items.Count, by);
        return record;
    }

    /// <summary>
    /// What the object was like before the changes this package carries: before the oldest
    /// change noticed since it last went into a package. Null when nothing is known.
    /// </summary>
    private async Task<string?> Base(AreaItem item, DateTimeOffset? since, string by, CancellationToken cancellation)
    {
        try
        {
            var body = await Ask("DatabaseChanges", by, request =>
            {
                if (!string.IsNullOrEmpty(item.Database))
                    request["Database"] = item.Database;
                request["Kind"] = item.ObjectKind;
                request["Schema"] = item.Schema;
                request["Name"] = item.Name;
                if (since is { } from)
                    request["From"] = from;
                request["Limit"] = 500;
            }, cancellation);
            // The latest first: the last one is the oldest.
            return (body["Changes"] as JsonArray)?.LastOrDefault()?["OldFingerprint"]?.GetValue<string>();
        }
        catch (TransportRefused)
        {
            return null;
        }
    }

    /// <summary>
    /// When each object last went into a package made here.
    /// </summary>
    public Dictionary<string, DateTimeOffset> LastPackaged()
    {
        var last = new Dictionary<string, DateTimeOffset>();
        foreach (var record in store.Packages().Where(record => !record.Received))
            foreach (var item in record.Manifest.Items.Where(item => item.Kind == "database" && item.Action != "Script"))
            {
                var key = Key(item.ObjectKind, item.Schema, item.Name);
                if (!last.TryGetValue(key, out var at) || record.Manifest.CreatedAt > at)
                    last[key] = record.Manifest.CreatedAt;
            }
        return last;
    }

    /// <summary>
    /// Types and tables before what is made of them, each object after the ones it uses that
    /// come in the same package, and what is dropped at the end.
    /// </summary>
    private static List<(AreaItem Area, PackageItem Item, byte[] Content)> Arrange(List<(AreaItem Area, PackageItem Item, byte[] Content)> items)
    {
        // The database first, so that what runs afterwards finds it as it expects; the web application last.
        int Rank((AreaItem Area, PackageItem Item, byte[] Content) item) =>
            item.Item.Kind == "frontend" ? Order.Length + 2
            : item.Item.Kind != "database" ? Order.Length + 1
            : item.Item.Action == "Drop" ? Order.Length : Math.Max(0, Array.IndexOf(Order, item.Item.Action == "Script" ? "Script" : item.Item.ObjectKind));

        var pending = items.Select((item, index) => (item, index)).OrderBy(pair => Rank(pair.item)).ThenBy(pair => pair.index).Select(pair => pair.item).ToList();
        var placed = new List<(AreaItem Area, PackageItem Item, byte[] Content)>();
        var done = new HashSet<string>();
        var inside = pending.Where(item => item.Item.Action == "Define").Select(item => Key(item.Item.ObjectKind, item.Item.Schema, item.Item.Name)).ToHashSet();

        while (pending.Count > 0)
        {
            // The first whose every need that comes in the package is already placed; when they go round in a circle, the first.
            var next = pending.FirstOrDefault(item => item.Item.Uses.All(use => use.Database is not null || !inside.Contains(Key(use.Kind, use.Schema, use.Name))
                || done.Contains(Key(use.Kind, use.Schema, use.Name)) || Key(use.Kind, use.Schema, use.Name) == Key(item.Item.ObjectKind, item.Item.Schema, item.Item.Name)));
            if (next.Item is null)
                next = pending[0];
            pending.Remove(next);
            placed.Add(next);
            if (next.Item.Action == "Define")
                done.Add(Key(next.Item.ObjectKind, next.Item.Schema, next.Item.Name));
        }
        return placed;
    }

    // ---------------------------------------------------------------- comparing

    /// <summary>
    /// How every item of a package stands against this environment: what is new here, what is
    /// already the same, what differs from where the change started, and what an item needs
    /// that is neither here nor in the package.
    /// </summary>
    public async Task<List<ItemCheck>> Check(PackageRecord record, string by, CancellationToken cancellation)
    {
        var here = record.Manifest.Items.Any(item => item.Kind == "database" && item.Action == "Define") ? await Everything(record, by, cancellation) : null;
        var brought = record.Manifest.Items.Where(item => item.Action == "Define").Select(item => Key(item.ObjectKind, item.Schema, item.Name)).ToHashSet();
        var checks = new List<ItemCheck>();
        foreach (var item in record.Manifest.Items)
        {
            if (item.Action == "Script")
            {
                checks.Add(new ItemCheck(item.Number, "script", null, [], null));
                continue;
            }
            if (item.Kind != "database")
            {
                try
                {
                    if (await AgainstTarget(item, by, cancellation) is not { } found)
                        checks.Add(new ItemCheck(item.Number, "missing", null, [], null));
                    else if (found.Target["Problem"]?.GetValue<string>() is { } problem)
                        checks.Add(new ItemCheck(item.Number, "unknown", found.Target["Version"]?.GetValue<string>(), [], problem));
                    else
                        checks.Add(new ItemCheck(item.Number, found.Changes.All(change => change.State == "same") ? "same" : "changes", found.Target["Version"]?.GetValue<string>(), [], null)
                        {
                            Added = found.Changes.Count(change => change.State == "added"), Changed = found.Changes.Count(change => change.State == "changed"), Removed = found.Changes.Count(change => change.State == "removed")
                        });
                }
                catch (TransportRefused refused)
                {
                    checks.Add(new ItemCheck(item.Number, "unknown", null, [], refused.Message));
                }
                continue;
            }
            try
            {
                var current = await Read(item.Database, item.ObjectKind, item.Schema, item.Name, by, cancellation);
                var state = item.Action == "Drop"
                    ? current is null ? "absent" : "drops"
                    : current is null ? "new"
                    : current.Fingerprint == item.Fingerprint ? "same"
                    : item.ObjectKind is "Table" or "Type" ? "blocked"
                    : item.Base is null ? "changes"
                    : current.Fingerprint == item.Base ? "clean" : "conflict";
                var missing = item.Action == "Drop" || here is null ? [] : item.Uses
                    .Where(use => use.Database is null && use.Kind is not null && !brought.Contains(Key(use.Kind, use.Schema, use.Name)) && !here.Contains(Key(use.Kind, use.Schema, use.Name)))
                    .Select(use => $"{use.Schema}.{use.Name}").Distinct().ToList();
                checks.Add(new ItemCheck(item.Number, state, current?.Fingerprint, missing, null));
            }
            catch (TransportRefused refused)
            {
                checks.Add(new ItemCheck(item.Number, "unknown", null, [], refused.Message));
            }
        }
        return checks;
    }

    /// <summary>
    /// Every object this environment has, to tell what an item needs and is not here. Null
    /// when that cannot be told.
    /// </summary>
    private async Task<HashSet<string>?> Everything(PackageRecord record, string by, CancellationToken cancellation)
    {
        try
        {
            var database = record.Manifest.Items.Select(item => item.Database).FirstOrDefault(name => !string.IsNullOrEmpty(name));
            var all = new HashSet<string>();
            for (var offset = 0; ; offset += 500)
            {
                var page = await Ask("DatabaseObjects", by, request =>
                {
                    if (database is not null)
                        request["Database"] = database;
                    request["Limit"] = 500;
                    request["Offset"] = offset;
                }, cancellation);
                var objects = page["Objects"] as JsonArray ?? [];
                foreach (var item in objects)
                    all.Add(Key(item?["Kind"]?.GetValue<string>(), item?["Schema"]?.GetValue<string>(), item?["Name"]?.GetValue<string>()));
                if (objects.Count < 500 || offset > 100_000)
                    return all;
            }
        }
        catch (TransportRefused)
        {
            return null;
        }
    }

    /// <summary>
    /// What an item brings beside what is here now, to be compared line by line.
    /// </summary>
    public async Task<(string? Brought, string? Current, string? CurrentFingerprint)> Compare(PackageRecord record, PackageItem item, string by, CancellationToken cancellation)
    {
        var brought = store.Script(record.Manifest.Id, item.Number);
        if (item.Action == "Script")
            return (brought, null, null);
        var current = await Read(item.Database, item.ObjectKind, item.Schema, item.Name, by, cancellation);
        return (item.Action == "Drop" ? null : brought, current?.Script, current?.Fingerprint);
    }

    // ---------------------------------------------------------------- approving and applying

    /// <summary>
    /// Approves a package to be applied now or at a given time. One that brings a table or a
    /// type this environment already has is not approved: applying it is sure to fail there.
    /// </summary>
    public async Task<PackageRecord> Approve(string id, DateTimeOffset? at, string by, CancellationToken cancellation)
    {
        var waiting = store.Find(id) ?? throw new TransportRefused("Não existe esse pacote neste ambiente", StatusCodes.Status404NotFound);
        var checks = Waits(waiting) ? await Check(waiting, by, cancellation) : [];
        if (checks.FirstOrDefault(check => check.State == "missing") is { } absent)
        {
            var item = waiting.Manifest.Items.First(candidate => candidate.Number == absent.Number);
            throw new TransportRefused($"O item {item.Number} ({item.Kind} {item.Name}) não existe neste ambiente. Instalar o que ainda não existe não é feito pelo transporte, por ora: instale-o aqui e aprove depois.", StatusCodes.Status409Conflict);
        }
        if (checks.FirstOrDefault(check => check.State == "blocked") is { } blocked)
        {
            var item = waiting.Manifest.Items.First(candidate => candidate.Number == blocked.Number);
            throw new TransportRefused($"O item {item.Number} ({item.Schema}.{item.Name}) é uma tabela ou um type que já existe neste ambiente e não pode ser recriado. A mudança precisa vir como um script de alteração, em outro pacote.", StatusCodes.Status409Conflict);
        }
        return Approve(id, at, by);
    }

    /// <summary>
    /// True while the package can still be approved here: one that arrived and waits, or one
    /// that was made here and was not applied here yet.
    /// </summary>
    private static bool Waits(PackageRecord record) => record.Status is "Pending" or "Reverted" || (record.Status == "Closed" && !record.Received);

    private PackageRecord Approve(string id, DateTimeOffset? at, string by)
    {
        var now = time.GetUtcNow();
        if (at is { } when && when < now.AddMinutes(-1))
            throw new TransportRefused("O horário escolhido já passou");
        return Change(id, record =>
        {
            if (!Waits(record))
                throw new TransportRefused("Este pacote não está mais aguardando aprovação neste ambiente", StatusCodes.Status409Conflict);
            record.Status = "Approved";
            record.ApprovedBy = by;
            record.ApplyAt = at ?? now;
            record.History.Add(new HistoryEntry { At = now, By = by, What = "approved", Detail = at is null ? "para aplicar agora" : $"para aplicar em {at.Value.ToLocalTime():dd/MM/yyyy HH:mm}" });
        });
    }

    public PackageRecord Reject(string id, string? reason, string by) => Change(id, record =>
    {
        // What was made here is not refused, only deleted; but an approval of it can be taken back like any other.
        if (!record.Received && record.Status != "Approved")
            throw new TransportRefused("Um pacote montado aqui não é recusado: ele pode ser excluído", StatusCodes.Status409Conflict);
        if (record.Status is not ("Pending" or "Approved"))
            throw new TransportRefused("Este pacote não está aguardando aprovação nem aplicação", StatusCodes.Status409Conflict);
        record.History.Add(new HistoryEntry { At = time.GetUtcNow(), By = by, What = record.Status == "Approved" ? "cancelled" : "rejected", Detail = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim() });
        // An approval that was taken back leaves the package waiting again; a refusal ends it.
        record.Status = record.Status != "Approved" ? "Rejected" : record.Received ? "Pending" : "Closed";
        record.ApplyAt = null;
        record.ApprovedBy = null;
    });

    private PackageRecord Change(string id, Action<PackageRecord> change) =>
        store.Update(id, change) ?? throw new TransportRefused("Não existe esse pacote neste ambiente", StatusCodes.Status404NotFound);

    /// <summary>
    /// What was being applied when the service stopped is not gone on with: nobody knows how
    /// far the last item went.
    /// </summary>
    public void Recover()
    {
        foreach (var record in store.Packages().Where(record => record.Status == "Applying"))
            store.Update(record.Manifest.Id, stuck =>
            {
                stuck.Status = stuck.Results.Any(result => result.Status == "applied") ? "Partial" : "Failed";
                stuck.History.Add(new HistoryEntry { At = time.GetUtcNow(), By = "ZapMQ", What = "interrupted", Detail = "o serviço parou durante a aplicação" });
            });
        foreach (var record in store.Packages().Where(record => record.Status == "Reverting"))
            store.Update(record.Manifest.Id, stuck =>
            {
                // The item that was being put back may or may not have been: nobody tries it again alone.
                foreach (var result in stuck.Results.Where(result => result.Reverted == "reverting"))
                {
                    result.Reverted = "unknown";
                    result.RevertProblem = "O serviço parou enquanto este item era revertido";
                }
                stuck.Status = AfterReverting(stuck);
                stuck.History.Add(new HistoryEntry { At = time.GetUtcNow(), By = "ZapMQ", What = "revert-interrupted", Detail = "o serviço parou durante a reversão" });
            });
    }

    // ---------------------------------------------------------------- putting back

    private static bool Stands(ItemResult result) => result.Status == "applied" && result.Reverted != "reverted";

    private static string AfterReverting(PackageRecord record) =>
        !record.Results.Any(Stands) ? "Reverted"
        : record.Results.Any(result => result.Reverted is "reverted" or "unknown") ? "RevertedPartly"
        : record.Results.Count(result => result.Status == "applied") == record.Manifest.Items.Count ? "Applied" : "Partial";

    private static bool SameTarget(PackageItem one, PackageItem other) =>
        one.Kind == other.Kind && (one.Kind != "database"
            ? string.Equals(one.Name, other.Name, StringComparison.OrdinalIgnoreCase)
            : one.Action != "Script" && other.Action != "Script" && string.Equals(one.ObjectKind, other.ObjectKind, StringComparison.OrdinalIgnoreCase)
              && string.Equals(one.Schema, other.Schema, StringComparison.OrdinalIgnoreCase) && string.Equals(one.Name, other.Name, StringComparison.OrdinalIgnoreCase)
              && string.Equals(one.Database ?? "", other.Database ?? "", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The package that was applied over the same thing after this one, and still stands.
    /// </summary>
    private string? Later(PackageRecord record, PackageItem item, ItemResult result) =>
        store.Packages()
            .Where(other => other.Manifest.Id != record.Manifest.Id)
            .FirstOrDefault(other => other.Results.Any(applied => Stands(applied) && applied.At > result.At
                && other.Manifest.Items.FirstOrDefault(candidate => candidate.Number == applied.Number) is { } touched && SameTarget(touched, item)))
            ?.Manifest.Name;

    /// <summary>
    /// What putting a package back would do, item by item, the last applied first. Nothing is changed.
    /// </summary>
    public async Task<List<RevertStep>> RevertPlan(PackageRecord record, string by, CancellationToken cancellation)
    {
        var steps = new List<RevertStep>();
        foreach (var result in record.Results.Where(Stands).OrderByDescending(result => result.Number))
        {
            var item = record.Manifest.Items.First(candidate => candidate.Number == result.Number);
            if (Later(record, item, result) is { } later)
            {
                steps.Add(new RevertStep(item.Number, "cannot", null, $"O pacote \"{later}\" foi aplicado depois sobre o mesmo item. Reverta-o antes."));
                continue;
            }

            if (item.Kind != "database")
            {
                if (string.IsNullOrEmpty(result.Backup))
                    steps.Add(new RevertStep(item.Number, "cannot", null, "A cópia de como estava antes não foi guardada"));
                else if (await AgainstTarget(item, by, cancellation) is not { } against)
                    steps.Add(new RevertStep(item.Number, "cannot", null, "Não existe mais neste ambiente"));
                else if (against.Changes.Any(change => change.State != "same"))
                    steps.Add(new RevertStep(item.Number, "changed", "files", "Os arquivos mudaram depois deste pacote; o que está lá agora fica guardado em uma cópia"));
                else
                    steps.Add(new RevertStep(item.Number, "can", "files", null));
                continue;
            }

            if (item.Action == "Script")
            {
                steps.Add(new RevertStep(item.Number, "cannot", null, "Um script livre não tem como ser desfeito sozinho: o contrário dele precisa ser escrito e levado em outro pacote"));
                continue;
            }
            if (string.Equals(item.ObjectKind, "Table", StringComparison.OrdinalIgnoreCase))
            {
                steps.Add(new RevertStep(item.Number, "cannot", null, "Tabela não é desfeita pelo painel, por causa dos dados que guarda"));
                continue;
            }

            var current = await Read(item.Database, item.ObjectKind, item.Schema, item.Name, by, cancellation);
            if (item.Action == "Drop")
                steps.Add(result.Previous is null
                    ? new RevertStep(item.Number, "cannot", null, "O que foi apagado não ficou guardado")
                    : current is null
                        ? new RevertStep(item.Number, "can", "recreate", null)
                        : new RevertStep(item.Number, "changed", "recreate", "Já existe de novo neste ambiente; volta a ser como era antes do pacote"));
            else if (result.Previous is null)
                steps.Add(current is null
                    ? new RevertStep(item.Number, "cannot", null, "Foi criado pelo pacote e já não existe mais")
                    : new RevertStep(item.Number, current.Fingerprint == item.Fingerprint ? "can" : "changed", "drop",
                        current.Fingerprint == item.Fingerprint ? null : "Foi alterado depois deste pacote; essa alteração se perde com ele"));
            else
                steps.Add(current is not null && current.Fingerprint == item.Fingerprint
                    ? new RevertStep(item.Number, "can", "previous", null)
                    : new RevertStep(item.Number, "changed", "previous", current is null ? "Não existe mais; volta a existir como era antes do pacote" : "Foi alterado depois deste pacote; essa alteração se perde"));
        }
        return steps;
    }

    /// <summary>
    /// Asks for what a package did here to be put back. It is done by the runner, one item at a
    /// time, the last applied first.
    /// </summary>
    public async Task<PackageRecord> Revert(string id, string by, CancellationToken cancellation)
    {
        var record = store.Find(id) ?? throw new TransportRefused("Não existe esse pacote neste ambiente", StatusCodes.Status404NotFound);
        if (record.Status is not ("Applied" or "Partial" or "RevertedPartly"))
            throw new TransportRefused("Só o que foi aplicado neste ambiente pode ser revertido", StatusCodes.Status409Conflict);
        if (!(await RevertPlan(record, by, cancellation)).Any(step => step.State != "cannot"))
            throw new TransportRefused("Nada do que este pacote fez pode ser revertido pelo painel", StatusCodes.Status409Conflict);
        return Change(id, reverting =>
        {
            if (reverting.Status is not ("Applied" or "Partial" or "RevertedPartly"))
                throw new TransportRefused("Só o que foi aplicado neste ambiente pode ser revertido", StatusCodes.Status409Conflict);
            reverting.Status = "Reverting";
            reverting.RevertBy = by;
            reverting.History.Add(new HistoryEntry { At = time.GetUtcNow(), By = by, What = "reverting" });
        });
    }

    private static void Outcome(ItemResult result, WorkerControlAnswer answer)
    {
        if (!answer.Reached)
        {
            // Asked and not answered: it may have been done, it may not.
            result.Reverted = "unknown";
            result.RevertProblem = answer.Problem;
        }
        else if (!answer.Ok)
        {
            result.Reverted = "failed";
            result.RevertProblem = answer.ErrorMessage;
        }
        else
        {
            var body = answer.Body!;
            result.Reverted = body["Applied"]?.GetValue<bool>() == true ? "reverted" : "failed";
            result.RevertProblem = body["Problem"]?.GetValue<string>();
            result.RevertBackup = body["Backup"]?.GetValue<string>();
            result.RevertMessages = [.. (body["Messages"] as JsonArray ?? []).Select(message => message?.GetValue<string>() ?? "")];
        }
    }

    private async Task Undo(string id, CancellationToken cancellation)
    {
        if (store.Find(id) is not { Status: "Reverting" } record)
            return;
        var by = record.RevertBy ?? "";
        logger.LogInformation("Transport: putting back what package {Name} ({Id}) did, asked by {User}", record.Manifest.Name, id, by);

        List<RevertStep> steps;
        string? stopped = null;
        try
        {
            steps = await RevertPlan(record, by, cancellation);
        }
        catch (TransportRefused refused)
        {
            steps = [];
            stopped = refused.Message;
        }

        foreach (var step in steps.Where(step => step.State != "cannot"))
        {
            var item = record.Manifest.Items.First(candidate => candidate.Number == step.Number);
            var previous = record.Results.First(result => result.Number == step.Number).Previous;
            var outcome = new ItemResult();
            store.Update(id, reverting => reverting.Results.First(result => result.Number == step.Number).Reverted = "reverting");
            try
            {
                if (step.How == "files")
                    Outcome(outcome, await worker.AskAsync("TransportRevert", by, ReplacePatience, request =>
                    {
                        request["Package"] = id;
                        request["Item"] = item.Number;
                        request["Kind"] = item.Kind;
                        request["Name"] = item.Name;
                        request["Backup"] = record.Results.First(result => result.Number == step.Number).Backup;
                    }, cancellation));
                else
                {
                    // What the package left there is kept, as what was there before it was.
                    outcome.RevertReplaced = (await Read(item.Database, item.ObjectKind, item.Schema, item.Name, by, cancellation))?.Script;
                    Outcome(outcome, await worker.AskAsync("DatabaseApply", by, ApplyPatience, request =>
                    {
                        request["Package"] = id;
                        request["Item"] = item.Number;
                        request["Action"] = step.How == "drop" ? "Drop" : "Define";
                        if (!string.IsNullOrEmpty(item.Database))
                            request["Database"] = item.Database;
                        request["Kind"] = item.ObjectKind;
                        request["Variety"] = item.Variety;
                        request["Schema"] = item.Schema;
                        request["Name"] = item.Name;
                        if (step.How != "drop")
                            request["Script"] = previous;
                    }, cancellation));
                }
            }
            catch (TransportRefused refused)
            {
                outcome.Reverted = "failed";
                outcome.RevertProblem = refused.Message;
            }

            store.Update(id, reverting =>
            {
                var result = reverting.Results.First(candidate => candidate.Number == step.Number);
                result.Reverted = outcome.Reverted;
                result.RevertProblem = outcome.RevertProblem;
                result.RevertMessages = outcome.RevertMessages;
                result.RevertBackup = outcome.RevertBackup;
                result.RevertReplaced = outcome.RevertReplaced;
                result.RevertedAt = time.GetUtcNow();
            });
            if (outcome.Reverted != "reverted")
            {
                stopped = $"parou no item {item.Number}: {outcome.RevertProblem}";
                break;
            }
        }

        var finished = store.Update(id, reverted =>
        {
            reverted.Status = AfterReverting(reverted);
            var done = reverted.Results.Count(result => result.Reverted == "reverted");
            var left = reverted.Results.Count(Stands);
            reverted.History.Add(new HistoryEntry
            {
                At = time.GetUtcNow(), By = by,
                What = reverted.Status == "Reverted" ? "reverted" : reverted.Status == "RevertedPartly" ? "reverted-partly" : "revert-failed",
                Detail = stopped ?? (left == 0 ? $"{done} item(s)" : $"{done} item(s) revertido(s); {left} ficaram como o pacote deixou")
            });
        });
        logger.LogInformation("Transport: putting back package {Name} ({Id}) ended as {Status}", record.Manifest.Name, id, finished?.Status);
    }

    /// <summary>
    /// Applies the packages whose time has come, one at a time, each item once.
    /// </summary>
    public async Task ApplyDue(CancellationToken cancellation)
    {
        foreach (var due in store.Packages().Where(record => record.Status == "Approved" && record.ApplyAt <= time.GetUtcNow()).OrderBy(record => record.ApplyAt))
            await Apply(due.Manifest.Id, cancellation);
        foreach (var asked in store.Packages().Where(record => record.Status == "Reverting"))
            await Undo(asked.Manifest.Id, cancellation);
    }

    /// <summary>
    /// For how long the replacing of what runs from a folder is waited for: it stops, copies,
    /// replaces, starts and waits for it to be up.
    /// </summary>
    public static readonly TimeSpan ReplacePatience = TimeSpan.FromMinutes(12);

    /// <summary>
    /// Hands the files of an item to the Worker Control, which stops the target, keeps a copy,
    /// replaces the files and starts it again.
    /// </summary>
    private async Task Replace(string id, PackageItem item, ItemResult result, string by, CancellationToken cancellation)
    {
        var files = store.Content(id, item.Number) ?? throw new TransportRefused("O conteúdo do item não está mais no arquivo do pacote");
        var work = store.WorkFile($"{id}-{item.Number:00}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(work)!);
        await File.WriteAllBytesAsync(work, files, cancellation);
        try
        {
            var answer = await worker.AskAsync("TransportDeploy", by, ReplacePatience, request =>
            {
                request["Package"] = id;
                request["Item"] = item.Number;
                request["Kind"] = item.Kind;
                request["Name"] = item.Name;
                request["File"] = work;
            }, cancellation);

            if (!answer.Reached)
            {
                result.Status = "unknown";
                result.Problem = answer.Problem;
            }
            else if (!answer.Ok)
            {
                result.Status = "failed";
                result.Problem = answer.ErrorMessage;
            }
            else
            {
                var body = answer.Body!;
                result.Status = body["Applied"]?.GetValue<bool>() == true ? "applied" : "failed";
                result.Did = body["Did"]?.GetValue<string>();
                result.Problem = body["Problem"]?.GetValue<string>();
                result.Backup = body["Backup"]?.GetValue<string>();
                result.Messages = [.. (body["Messages"] as JsonArray ?? []).Select(message => message?.GetValue<string>() ?? "")];
            }
        }
        finally
        {
            File.Delete(work);
        }
    }

    private async Task Apply(string id, CancellationToken cancellation)
    {
        var record = store.Update(id, starting =>
        {
            if (starting.Status != "Approved")
                return;
            starting.Status = "Applying";
            starting.Results.Clear();
            starting.History.Add(new HistoryEntry { At = time.GetUtcNow(), By = starting.ApprovedBy ?? "", What = "applying" });
        });
        if (record is not { Status: "Applying" })
            return;
        var by = record.ApprovedBy ?? "";
        logger.LogInformation("Transport: applying package {Name} ({Id}), approved by {User}", record.Manifest.Name, id, by);

        foreach (var item in record.Manifest.Items)
        {
            var result = new ItemResult { Number = item.Number };
            try
            {
                if (item.Kind != "database")
                {
                    await Replace(id, item, result, by, cancellation);
                    result.At = time.GetUtcNow();
                    store.Update(id, applying => applying.Results.Add(result));
                    if (result.Status != "applied")
                        break;
                    continue;
                }

                var script = store.Script(id, item.Number) ?? throw new TransportRefused("O conteúdo do item não está mais no arquivo do pacote");
                if (item.Action != "Script")
                {
                    // Kept before anything is touched, to put back by hand if it comes to that.
                    var before = await Read(item.Database, item.ObjectKind, item.Schema, item.Name, by, cancellation);
                    result.Previous = before?.Script;
                    result.PreviousFingerprint = before?.Fingerprint;
                }

                var answer = await worker.AskAsync("DatabaseApply", by, ApplyPatience, request =>
                {
                    request["Package"] = id;
                    request["Item"] = item.Number;
                    request["Action"] = item.Action;
                    if (!string.IsNullOrEmpty(item.Database))
                        request["Database"] = item.Database;
                    request["Kind"] = item.ObjectKind;
                    request["Variety"] = item.Variety;
                    request["Schema"] = item.Schema;
                    request["Name"] = item.Name;
                    if (item.Action != "Drop")
                        request["Script"] = script;
                }, cancellation);

                if (!answer.Reached)
                {
                    // Asked and not answered: it may have been done, it may not. Nobody tries again on their own.
                    result.Status = "unknown";
                    result.Problem = answer.Problem;
                }
                else if (!answer.Ok)
                {
                    result.Status = "failed";
                    result.Problem = answer.ErrorMessage;
                }
                else
                {
                    var body = answer.Body!;
                    result.Status = body["Applied"]?.GetValue<bool>() == true ? "applied" : "failed";
                    result.Did = body["Did"]?.GetValue<string>();
                    result.Problem = body["Problem"]?.GetValue<string>();
                    result.Batch = body["Batch"]?.GetValue<int?>();
                    result.Line = body["Line"]?.GetValue<int?>();
                    result.Messages = [.. (body["Messages"] as JsonArray ?? []).Select(message => message?.GetValue<string>() ?? "")];
                }
            }
            catch (TransportRefused refused)
            {
                result.Status = "failed";
                result.Problem = refused.Message;
            }

            result.At = time.GetUtcNow();
            store.Update(id, applying => applying.Results.Add(result));
            if (result.Status != "applied")
                break;
        }

        var finished = store.Update(id, applied =>
        {
            var done = applied.Results.Count(result => result.Status == "applied");
            applied.Status = done == applied.Manifest.Items.Count ? "Applied" : done == 0 ? "Failed" : "Partial";
            var last = applied.Results.LastOrDefault();
            applied.History.Add(new HistoryEntry
            {
                At = time.GetUtcNow(), By = by, What = applied.Status.ToLowerInvariant(),
                Detail = applied.Status == "Applied" ? $"{done} item(s)" : $"{done} de {applied.Manifest.Items.Count} item(s); parou no item {last?.Number}: {last?.Problem}"
            });
        });
        logger.LogInformation("Transport: package {Name} ({Id}) ended as {Status}", record.Manifest.Name, id, finished?.Status);
    }
}

/// <summary>
/// Looks, every few seconds, for a package whose time to be applied has come.
/// </summary>
public sealed class TransportRunner(TransportService transport, ILogger<TransportRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        transport.Recover();
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await transport.ApplyDue(stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogError(error, "Applying the packages that were due failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), stopping);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
