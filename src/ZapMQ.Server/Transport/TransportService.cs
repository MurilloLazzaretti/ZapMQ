using System.Text.Json.Nodes;
using ZapMQ.Server.Panel;

namespace ZapMQ.Server.Transport;

/// <summary>
/// How one item of a package stands against what this environment has.
/// </summary>
public sealed record ItemCheck(int Number, string State, string? CurrentFingerprint, IReadOnlyList<string> Missing, string? Problem);

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
        var chosen = ids is { Count: > 0 } ? ids.Select(id => area.FirstOrDefault(item => item.Id == id) ?? throw new TransportRefused("Um dos itens escolhidos não está mais na área")).ToList() : area;
        if (chosen.Count == 0)
            throw new TransportRefused("A área está vazia: não há o que fechar");

        var now = time.GetUtcNow();
        var packaged = LastPackaged();
        var items = new List<(AreaItem Area, PackageItem Item, string Script)>();
        foreach (var area_ in chosen)
        {
            var item = new PackageItem
            {
                Kind = area_.Kind, Action = area_.Action, ObjectKind = area_.ObjectKind, Variety = area_.Variety, Schema = area_.Schema, Name = area_.Name, Title = area_.Title, Database = area_.Database
            };
            string script;
            if (area_.Action == "Script")
                script = area_.Script ?? "";
            else if (area_.Action == "Drop")
                script = $"-- {area_.ObjectKind} {area_.Schema}.{area_.Name}: apagado em {store.Environment}\n";
            else
            {
                var found = await Read(area_.Database, area_.ObjectKind, area_.Schema, area_.Name, by, cancellation)
                    ?? throw new TransportRefused($"{area_.Schema}.{area_.Name} não existe mais neste banco. Tire-o da área, ou inclua-o como exclusão.");
                script = found.Script;
                item.Fingerprint = found.Fingerprint;
                item.Variety = found.Variety;
                item.Uses = found.Uses;
                item.Base = await Base(area_, packaged.GetValueOrDefault(Key(area_.ObjectKind, area_.Schema, area_.Name)), by, cancellation);
            }
            items.Add((area_, item, script));
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
        var record = store.Close(manifest, [.. items.Select(item => item.Script)], chosen.Select(item => item.Id), by, now);
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
            foreach (var item in record.Manifest.Items.Where(item => item.Action != "Script"))
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
    private static List<(AreaItem Area, PackageItem Item, string Script)> Arrange(List<(AreaItem Area, PackageItem Item, string Script)> items)
    {
        int Rank((AreaItem Area, PackageItem Item, string Script) item) =>
            item.Item.Action == "Drop" ? Order.Length : Math.Max(0, Array.IndexOf(Order, item.Item.Action == "Script" ? "Script" : item.Item.ObjectKind));

        var pending = items.Select((item, index) => (item, index)).OrderBy(pair => Rank(pair.item)).ThenBy(pair => pair.index).Select(pair => pair.item).ToList();
        var placed = new List<(AreaItem Area, PackageItem Item, string Script)>();
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
        var here = await Everything(record, by, cancellation);
        var brought = record.Manifest.Items.Where(item => item.Action == "Define").Select(item => Key(item.ObjectKind, item.Schema, item.Name)).ToHashSet();
        var checks = new List<ItemCheck>();
        foreach (var item in record.Manifest.Items)
        {
            if (item.Action == "Script")
            {
                checks.Add(new ItemCheck(item.Number, "script", null, [], null));
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
        if (waiting.Status == "Pending" && (await Check(waiting, by, cancellation)).FirstOrDefault(check => check.State == "blocked") is { } blocked)
        {
            var item = waiting.Manifest.Items.First(candidate => candidate.Number == blocked.Number);
            throw new TransportRefused($"O item {item.Number} ({item.Schema}.{item.Name}) é uma tabela ou um type que já existe neste ambiente e não pode ser recriado. A mudança precisa vir como um script de alteração, em outro pacote.", StatusCodes.Status409Conflict);
        }
        return Approve(id, at, by);
    }

    private PackageRecord Approve(string id, DateTimeOffset? at, string by)
    {
        var now = time.GetUtcNow();
        if (at is { } when && when < now.AddMinutes(-1))
            throw new TransportRefused("O horário escolhido já passou");
        return Change(id, record =>
        {
            if (record.Status != "Pending")
                throw new TransportRefused(record.Received ? "Este pacote não está mais aguardando aprovação" : "Um pacote só é aprovado no ambiente em que chegou, não onde foi montado", StatusCodes.Status409Conflict);
            record.Status = "Approved";
            record.ApprovedBy = by;
            record.ApplyAt = at ?? now;
            record.History.Add(new HistoryEntry { At = now, By = by, What = "approved", Detail = at is null ? "para aplicar agora" : $"para aplicar em {at.Value.ToLocalTime():dd/MM/yyyy HH:mm}" });
        });
    }

    public PackageRecord Reject(string id, string? reason, string by) => Change(id, record =>
    {
        if (!record.Received)
            throw new TransportRefused("Um pacote montado aqui não é recusado: ele pode ser excluído", StatusCodes.Status409Conflict);
        if (record.Status is not ("Pending" or "Approved"))
            throw new TransportRefused("Este pacote não está aguardando aprovação nem aplicação", StatusCodes.Status409Conflict);
        record.History.Add(new HistoryEntry { At = time.GetUtcNow(), By = by, What = record.Status == "Approved" ? "cancelled" : "rejected", Detail = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim() });
        // An approval that was taken back leaves the package waiting again; a refusal ends it.
        record.Status = record.Status == "Approved" ? "Pending" : "Rejected";
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
    }

    /// <summary>
    /// Applies the packages whose time has come, one at a time, each item once.
    /// </summary>
    public async Task ApplyDue(CancellationToken cancellation)
    {
        foreach (var due in store.Packages().Where(record => record.Status == "Approved" && record.ApplyAt <= time.GetUtcNow()).OrderBy(record => record.ApplyAt))
            await Apply(due.Manifest.Id, cancellation);
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
