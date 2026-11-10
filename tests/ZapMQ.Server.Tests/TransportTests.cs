using Microsoft.Extensions.DependencyInjection;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ZapMQ.Server.Tests;

/// <summary>
/// Changes carried in packages: made from the objects of a database, closed, taken to another
/// environment as a file, compared with what is there, approved and applied. The Worker
/// Control, and the database behind it, are pretend ones.
/// </summary>
public sealed class TransportTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private HttpClient Api => server.Admin;

    /// <summary>
    /// A database that is whatever the test says, behind a Worker Control that answers for it.
    /// </summary>
    private sealed class PretendDatabase
    {
        public Dictionary<string, (string Kind, string Script, string[] Uses)> Objects { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<JObject> Applied { get; } = [];
        public List<JObject> Changes { get; } = [];
        public Func<JObject, JObject?>? OnApply { get; set; }
        public List<JObject> Reverted { get; } = [];
        public Func<JObject, JObject?>? OnRevert { get; set; }
        public string[] Databases { get; set; } = ["Sales"];
        /// <summary>
        /// What can be replaced on the machine, as "kind:name", each with its files.
        /// </summary>
        public Dictionary<string, Dictionary<string, string>> Targets { get; } = [];

        /// <summary>
        /// The configuration files of the environment each target has now, as "kind:name", each with its text.
        /// </summary>
        public Dictionary<string, Dictionary<string, string>> EnvironmentHere { get; } = [];

        /// <summary>
        /// A Worker Control from before the configuration of the environment travelled: it does not list it.
        /// </summary>
        public bool OldWorker { get; set; }

        /// <summary>
        /// The map of the endpoints of the API Cadastro, and the OpenAPI document it serves.
        /// </summary>
        public JObject? ApiMap { get; set; }
        public JObject? OpenApi { get; set; }
        public string? TargetProblem { get; set; }

        /// <summary>
        /// What the machine says about itself to install a target elsewhere: sites with their ports, the files of
        /// configuration of each target (as "kind:name") and of the proxy.
        /// </summary>
        public JObject Defaults { get; } = new() { ["Ok"] = true, ["Allowed"] = true, ["SiteName"] = "{name} {n}" };
        public Dictionary<string, Dictionary<string, string>> Settings { get; } = [];
        public Dictionary<string, JArray> Sites { get; } = [];
        public Dictionary<string, string> ProxyFiles { get; } = [];
        /// <summary>
        /// What a whole web application is, as "kind:name": mfe or monolith.
        /// </summary>
        public Dictionary<string, string> Modes { get; } = [];

        /// <summary>
        /// What was left in the inbox of the machine for a target, as "kind:name".
        /// </summary>
        public Dictionary<string, Dictionary<string, string>> Incoming { get; } = [];
        public Dictionary<string, string>? Deployed { get; private set; }

        public static string Sha(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        public static byte[] Zipped(Dictionary<string, string> files, string outer = "")
        {
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (path, text) in files)
                {
                    using var writer = new StreamWriter(zip.CreateEntry(outer + path).Open(), new UTF8Encoding(false));
                    writer.Write(text);
                }
            return memory.ToArray();
        }

        public static Dictionary<string, string> Unzipped(byte[] bytes)
        {
            using var zip = new ZipArchive(new MemoryStream(bytes));
            return zip.Entries.ToDictionary(entry => entry.FullName, entry => new StreamReader(entry.Open()).ReadToEnd());
        }

        public static string Print(string script) => "fp-" + script.GetHashCode().ToString("x8");

        public JObject Answer(JObject request)
        {
            switch ((string?)request["Command"])
            {
                case "DatabaseObject":
                {
                    if (request["Database"] is { } asked && !Databases.Contains((string)asked!))
                        return new JObject { ["Ok"] = false, ["Error"] = new JObject { ["Code"] = "unknown-database", ["Message"] = "not watched" } };
                    var name = (string)request["Name"]!;
                    if (!Objects.TryGetValue(name, out var found) || found.Kind != (string?)request["Kind"])
                        return new JObject { ["Ok"] = false, ["Error"] = new JObject { ["Code"] = "not-found", ["Message"] = "no such object" } };
                    return new JObject
                    {
                        ["Ok"] = true, ["Script"] = found.Script, ["Fingerprint"] = Print(found.Script),
                        ["Object"] = new JObject { ["Variety"] = found.Kind[..1] },
                        ["Uses"] = new JArray(found.Uses.Select(use => new JObject { ["Schema"] = "dbo", ["Name"] = use, ["Kind"] = Objects.TryGetValue(use, out var used) ? used.Kind : "Table" }))
                    };
                }
                case "Database":
                    return new JObject { ["Ok"] = true, ["Databases"] = new JArray(Databases) };
                case "DatabaseInventory":
                    return new JObject { ["Ok"] = true, ["Database"] = Databases[0], ["Objects"] = new JArray(Objects.Select(pair => new JObject { ["Kind"] = pair.Value.Kind, ["Schema"] = "dbo", ["Name"] = pair.Key })) };
                case "ApiDocs":
                    return new JObject
                    {
                        ["Ok"] = true, ["Kind"] = request["Kind"], ["Name"] = request["Name"], ["Map"] = ApiMap, ["MapProblem"] = ApiMap is null ? "absent" : null,
                        ["OpenApi"] = OpenApi, ["OpenApiProblem"] = OpenApi is null ? "absent" : null
                    };
                case "ApiMaps":
                    return new JObject { ["Ok"] = true, ["Applications"] = ApiMap is null ? new JArray() : new JArray(new JObject { ["Kind"] = "api", ["Name"] = "Cadastro", ["Map"] = ApiMap }) };
                case "DatabaseObjects":
                    return new JObject { ["Ok"] = true, ["Objects"] = new JArray(Objects.Select(pair => new JObject { ["Kind"] = pair.Value.Kind, ["Schema"] = "dbo", ["Name"] = pair.Key })) };
                case "DatabaseChanges":
                    return new JObject { ["Ok"] = true, ["Changes"] = new JArray(Changes.Where(change => (string?)change["Name"] == (string?)request["Name"])) };
                case "TransportTargets":
                    return new JObject { ["Ok"] = true, ["Targets"] = new JArray(Targets.Select(pair => new JObject { ["Kind"] = pair.Key.Split(':')[0], ["Name"] = pair.Key.Split(':')[1], ["Version"] = "1.0" })) };
                case "TransportTarget":
                    return Targets.TryGetValue($"{request["Kind"]}:{request["Name"]}", out var files)
                        ? new JObject
                        {
                            ["Ok"] = true, ["Target"] = new JObject { ["Kind"] = request["Kind"], ["Name"] = request["Name"], ["Version"] = "1.0", ["Problem"] = TargetProblem, ["Paths"] = new JArray("D:\\Apps\\" + request["Name"]), ["Sites"] = Sites.GetValueOrDefault((string)request["Name"]!) ?? [], ["Mode"] = Modes.GetValueOrDefault($"{request["Kind"]}:{request["Name"]}") },
                            ["Files"] = new JArray(files.Select(file => new JObject { ["Path"] = file.Key, ["Size"] = file.Value.Length, ["Sha256"] = Sha(file.Value) })),
                            ["Environment"] = OldWorker ? null : new JArray((EnvironmentHere.GetValueOrDefault($"{request["Kind"]}:{request["Name"]}") ?? []).Select(file => new JObject { ["Path"] = file.Key, ["Size"] = file.Value.Length, ["Sha256"] = Sha(file.Value) }))
                        }
                        : new JObject { ["Ok"] = false, ["Error"] = new JObject { ["Code"] = "not-found", ["Message"] = "no such target" } };
                case "TransportCapture":
                {
                    var running = (bool?)request["Incoming"] == true ? Incoming[$"{request["Kind"]}:{request["Name"]}"] : Targets[$"{request["Kind"]}:{request["Name"]}"];
                    var file = Path.Combine(Path.GetTempPath(), "zapmq-capture-" + Guid.NewGuid().ToString("N") + ".zip");
                    File.WriteAllBytes(file, Zipped(running));
                    return new JObject { ["Ok"] = true, ["File"] = file, ["Target"] = new JObject { ["Kind"] = request["Kind"], ["Name"] = request["Name"], ["Version"] = "2.0" } };
                }
                case "TransportDeploy":
                    lock (Applied)
                        Applied.Add(request);
                    // The files are there to be read while the answer is being made, and not after.
                    Deployed = Unzipped(File.ReadAllBytes((string)request["File"]!));
                    if (OnApply is null)
                        // The Worker Control lists as the files of a target the ones that are not of the environment; the others it keeps apart.
                        Targets[$"{request["Kind"]}:{request["Name"]}"] = Deployed.Where(file => !global::ZapMQ.Server.Transport.TransportService.IsEnvironmentFile(file.Key)).ToDictionary(file => file.Key, file => file.Value);
                    return OnApply?.Invoke(request) ?? new JObject { ["Ok"] = true, ["Applied"] = true, ["Did"] = "replaced", ["Backup"] = "/backup/there", ["Messages"] = new JArray("stopped", "replaced", "started") };
                case "ApplicationDefaults":
                    return Defaults;
                case "TransportFiles":
                    return new JObject { ["Ok"] = true, ["Files"] = new JArray((Settings.GetValueOrDefault($"{request["Kind"]}:{request["Name"]}") ?? []).Keys.Select(path => new JObject { ["Instance"] = 0, ["Path"] = path })) };
                case "TransportFile":
                    return new JObject { ["Ok"] = true, ["Content"] = (EnvironmentHere.GetValueOrDefault($"{request["Kind"]}:{request["Name"]}") ?? Settings[$"{request["Kind"]}:{request["Name"]}"])[(string)request["Path"]!] };
                case "Proxy":
                    return new JObject { ["Ok"] = true, ["Configured"] = ProxyFiles.Count > 0, ["Files"] = new JArray(ProxyFiles.Keys.Select(path => new JObject { ["Path"] = path })) };
                case "ProxyFile":
                    return new JObject { ["Ok"] = true, ["Content"] = ProxyFiles[(string)request["Path"]!] };
                case "ApplicationCreate":
                    lock (Applied)
                        Applied.Add(request);
                    Deployed = Unzipped(File.ReadAllBytes((string)request["File"]!));
                    return OnApply?.Invoke(request) ?? new JObject { ["Ok"] = true, ["Created"] = true, ["Messages"] = new JArray("put the files") };
                case "ApplicationRemove":
                    lock (Applied)
                        Applied.Add(request);
                    return new JObject { ["Ok"] = true, ["Removed"] = true, ["Kept"] = "/removed/there" };
                case "TransportRevert":
                    lock (Reverted)
                        Reverted.Add(request);
                    return OnRevert?.Invoke(request) ?? new JObject { ["Ok"] = true, ["Applied"] = true, ["Did"] = "reverted", ["Backup"] = "/backup/before-going-back", ["Messages"] = new JArray("stopped", "put back", "started") };
                case "DatabaseApply":
                    lock (Applied)
                        Applied.Add(request);
                    if (OnApply?.Invoke(request) is { } said)
                        return said;
                    if ((string?)request["Action"] == "Drop")
                        Objects.Remove((string)request["Name"]!);
                    else if ((string?)request["Action"] == "Define")
                        Objects[(string)request["Name"]!] = ((string)request["Kind"]!, (string)request["Script"]!, []);
                    return new JObject { ["Ok"] = true, ["Applied"] = true, ["Did"] = "altered", ["Messages"] = new JArray("done") };
                default:
                    return new JObject { ["Ok"] = true };
            }
        }
    }

    private async Task<V2Client> Serve(PretendDatabase database)
    {
        var client = await V2Client.ConnectAsync(server.Port, "WorkerControl");
        await client.RequestAsync("bind", "WorkerControlAdmin");
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var delivery = await client.NextPushAsync();
                var answer = database.Answer((JObject)delivery["message"]!["body"]!);
                await client.RequestAsync("respond", "WorkerControlAdmin", frame =>
                {
                    frame["messageId"] = delivery["message"]!["id"];
                    frame["response"] = answer;
                });
            }
        });
        return client;
    }

    /// <summary>
    /// A package made here, released by whoever made it, as it goes to another environment.
    /// </summary>
    private async Task<byte[]> Released(string? id)
    {
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/release", new { }));
        return await Api.GetByteArrayAsync($"api/transport/packages/{id}/download");
    }

    private async Task<JObject> Json(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return JObject.Parse(text);
    }

    /// <summary>
    /// The open package the test puts things in.
    /// </summary>
    private string? _draft;

    private async Task ClearArea()
    {
        foreach (var draft in JObject.Parse(await Api.GetStringAsync("api/transport/drafts"))["drafts"]!)
            await Api.DeleteAsync("api/transport/drafts/" + (string?)draft["id"]);
        _draft = (string?)(await Json(await Api.PostAsJsonAsync("api/transport/drafts", new { name = "Teste" }), HttpStatusCode.Created))["id"];
    }

    /// <summary>
    /// The same package as another environment would have made it: under another identity and origin.
    /// </summary>
    /// <summary>
    /// A package as it arrives at one of several environments the test pretends there are, all
    /// served by the same panel: its versions are of a line of that environment alone.
    /// </summary>
    private static byte[] At(string environment, byte[] package) => FromElsewhere(package, line: environment);

    private static byte[] FromElsewhere(byte[] package, Func<string, string>? tamper = null, string? line = null)
    {
        using var source = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        using var memory = new MemoryStream();
        using (var target = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in source.Entries)
            {
                using var content = new MemoryStream();
                using (var stream = entry.Open())
                    stream.CopyTo(content);
                var bytes = content.ToArray();
                if (entry.FullName == "package.json")
                {
                    var manifest = JObject.Parse(Encoding.UTF8.GetString(bytes));
                    manifest["id"] = Guid.NewGuid().ToString("N");
                    manifest["origin"] = "DEV";
                    if (line is not null)
                        manifest["line"] = $"{manifest["line"]}-{line}";
                    bytes = Encoding.UTF8.GetBytes(manifest.ToString());
                }
                else if (tamper is not null && entry.FullName.EndsWith(".sql"))
                    bytes = Encoding.UTF8.GetBytes(tamper(Encoding.UTF8.GetString(bytes)));
                using var written = target.CreateEntry(entry.FullName).Open();
                written.Write(bytes);
            }
        return memory.ToArray();
    }

    private async Task<JObject> Import(byte[] package, HttpStatusCode expected = HttpStatusCode.Created) =>
        await Json(await Api.PostAsync("api/transport/packages/import", new ByteArrayContent(package)), expected);

    private async Task<JObject> WaitFor(string id, params string[] statuses)
    {
        for (var attempt = 0; attempt < 150; attempt++)
        {
            var detail = JObject.Parse(await Api.GetStringAsync("api/transport/packages/" + id));
            if (statuses.Contains((string?)detail["package"]!["status"]))
                return detail;
            await Task.Delay(100);
        }
        throw new TimeoutException("The package never got to " + string.Join(" or ", statuses));
    }

    [Fact]
    public async Task A_package_is_made_from_the_objects_of_the_database_in_the_order_they_depend_on_each_other()
    {
        var database = new PretendDatabase();
        database.Objects["spClose"] = ("Procedure", "CREATE PROCEDURE dbo.spClose AS SELECT * FROM dbo.vwOrders", ["vwOrders", "Orders"]);
        database.Objects["vwOrders"] = ("View", "CREATE VIEW dbo.vwOrders AS SELECT * FROM dbo.Orders", ["Orders", "fncTotal"]);
        database.Objects["fncTotal"] = ("Function", "CREATE FUNCTION dbo.fncTotal() RETURNS int AS BEGIN RETURN 1 END", []);
        database.Objects["OrderList"] = ("Type", "CREATE TYPE dbo.OrderList AS TABLE (Id int)", []);
        database.Changes.Add(new JObject { ["Name"] = "spClose", ["OldFingerprint"] = "fp-latest" });
        database.Changes.Add(new JObject { ["Name"] = "spClose", ["OldFingerprint"] = "fp-before-all" });
        await using var _ = await Serve(database);
        await ClearArea();

        // The screens say which database the object is in; with one database, the package names none.
        foreach (var (kind, name) in new[] { ("Procedure", "spClose"), ("View", "vwOrders"), ("Type", "OrderList"), ("Function", "fncTotal") })
            Assert.Equal(JTokenType.Null, (await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { draft = _draft, database = "Sales", kind, schema = "dbo", name }), HttpStatusCode.Created))["database"]!.Type);
        // Twice is once; what is not there cannot be carried; what is there cannot go as a drop.
        await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { draft = _draft, kind = "View", schema = "dbo", name = "VWORDERS" }), HttpStatusCode.Created);
        await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { draft = _draft, kind = "View", schema = "dbo", name = "vwGone" }), HttpStatusCode.NotFound);
        await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { draft = _draft, kind = "View", schema = "dbo", name = "vwOrders", drop = true }), HttpStatusCode.BadRequest);
        await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { draft = _draft, kind = "Procedure", schema = "dbo", name = "spOld", drop = true }), HttpStatusCode.Created);
        await Json(await Api.PostAsJsonAsync("api/transport/area/scripts", new { draft = _draft, title = "Nova coluna", script = "ALTER TABLE dbo.Orders ADD Channel varchar(10) NULL" }), HttpStatusCode.Created);
        await Json(await Api.PostAsJsonAsync("api/transport/area/scripts", new { draft = _draft, title = "", script = "x" }), HttpStatusCode.BadRequest);
        Assert.Equal(6, JObject.Parse(await Api.GetStringAsync("api/transport/area"))["items"]!.Count());

        // The script is the one of the moment the package is closed.
        database.Objects["fncTotal"] = ("Function", "CREATE FUNCTION dbo.fncTotal() RETURNS int AS BEGIN RETURN 2 END", []);
        // Without an open package, nothing is closed; without a name of its own, it keeps the one of the open package.
        await Json(await Api.PostAsJsonAsync("api/transport/packages", new { draft = "nenhum", name = "x" }), HttpStatusCode.NotFound);
        var closed = await Json(await Api.PostAsJsonAsync("api/transport/packages", new { draft = _draft, name = "Fechamento de pedidos", description = "Nova regra" }), HttpStatusCode.Created);

        Assert.Equal(("Closed", false, "TEST", "admin"), ((string?)closed["package"]!["status"], (bool)closed["package"]!["received"]!, (string?)closed["package"]!["origin"], (string?)closed["package"]!["createdBy"]));
        Assert.Equal(["OrderList", null, "fncTotal", "vwOrders", "spClose", "spOld"], closed["items"]!.Select(item => (string?)item["name"]));
        Assert.Equal(["Define", "Script", "Define", "Define", "Define", "Drop"], closed["items"]!.Select(item => (string?)item["action"]));
        Assert.Equal("fp-before-all", (string?)closed["items"]![4]!["base"]);
        Assert.Empty(JObject.Parse(await Api.GetStringAsync("api/transport/area"))["items"]!);
        var id = (string)closed["package"]!["id"]!;
        var third = JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{id}/items/3"));
        Assert.Contains("RETURN 2", (string?)third["script"]);

        // The file is a package that says what it carries and is whole.
        var file = await Released(id);
        using (var zip = new ZipArchive(new MemoryStream(file)))
        {
            Assert.Equal(7, zip.Entries.Count);
            Assert.NotNull(zip.GetEntry("items/02/script.sql"));
        }
        // Where it was made it is not taken in again.
        await Import(file, HttpStatusCode.Conflict);
        Assert.True(JObject.Parse(await Api.GetStringAsync("api/transport/packaged")).ContainsKey("procedure|dbo|spclose"));
    }

    private async Task<(PretendDatabase Origin, byte[] Package)> Made()
    {
        var origin = new PretendDatabase();
        origin.Objects["fncTotal"] = ("Function", "CREATE FUNCTION dbo.fncTotal() RETURNS int AS BEGIN RETURN 2 END", ["Rates"]);
        origin.Objects["spClose"] = ("Procedure", "CREATE PROCEDURE dbo.spClose AS SELECT dbo.fncTotal()", ["fncTotal"]);
        origin.Objects["spNew"] = ("Procedure", "CREATE PROCEDURE dbo.spNew AS RETURN", []);
        origin.Changes.Add(new JObject { ["Name"] = "spClose", ["OldFingerprint"] = PretendDatabase.Print("CREATE PROCEDURE dbo.spClose AS RETURN") });
        origin.Changes.Add(new JObject { ["Name"] = "fncTotal", ["OldFingerprint"] = "fp-what-dev-had" });
        await using var worker = await Serve(origin);
        await ClearArea();
        foreach (var (kind, name) in new[] { ("Function", "fncTotal"), ("Procedure", "spClose"), ("Procedure", "spNew") })
            await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { draft = _draft, kind, schema = "dbo", name }), HttpStatusCode.Created);
        await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { draft = _draft, kind = "View", schema = "dbo", name = "vwOld", drop = true }), HttpStatusCode.Created);
        var closed = await Json(await Api.PostAsJsonAsync("api/transport/packages", new { draft = _draft, name = "Pacote " + Guid.NewGuid().ToString("N")[..6] }), HttpStatusCode.Created);
        return (origin, await Released((string?)closed["package"]!["id"]));
    }

    [Fact]
    public async Task A_package_that_arrives_is_compared_with_what_is_here_then_approved_and_applied()
    {
        var (_, made) = await Made();
        // The destination: one object as the change found it, one changed by somebody else, one missing, one to drop.
        var here = new PretendDatabase();
        here.Objects["spClose"] = ("Procedure", "CREATE PROCEDURE dbo.spClose AS RETURN", []);
        here.Objects["fncTotal"] = ("Function", "CREATE FUNCTION dbo.fncTotal() RETURNS int AS BEGIN RETURN 99 END", []);
        here.Objects["vwOld"] = ("View", "CREATE VIEW dbo.vwOld AS SELECT 1 AS a", []);
        await using var _ = await Serve(here);

        var received = await Import(FromElsewhere(made));
        var id = (string)received["package"]!["id"]!;
        Assert.Equal(("Pending", true, "DEV"), ((string?)received["package"]!["status"], (bool)received["package"]!["received"]!, (string?)received["package"]!["origin"]));

        var checks = JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{id}/check"))["checks"]!;
        Assert.Equal(["conflict", "clean", "new", "drops"], checks.Select(check => (string?)check["state"]));
        // The function uses a table that is neither here nor in the package.
        Assert.Equal(["dbo.Rates"], checks[0]!["missing"]!.Select(name => (string?)name));
        Assert.Empty(checks[1]!["missing"]!);

        var compared = JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{id}/items/2"));
        Assert.Contains("fncTotal()", (string?)compared["script"]);
        Assert.Equal("CREATE PROCEDURE dbo.spClose AS RETURN", (string?)compared["current"]);

        // What the function needs is not here: it is not approved until it is.
        var refused = await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new { }), HttpStatusCode.Conflict);
        Assert.Contains("dbo.Rates", (string?)refused["error"]);
        here.Objects["Rates"] = ("Table", "CREATE TABLE dbo.Rates (Id int)", []);
        var approved = await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new { }));
        Assert.Equal("admin", (string?)approved["package"]!["approvedBy"]);
        var applied = await WaitFor(id, "Applied", "Partial", "Failed");

        Assert.Equal("Applied", (string?)applied["package"]!["status"]);
        Assert.Equal(["applied", "applied", "applied", "applied"], applied["results"]!.Select(result => (string?)result["status"]));
        lock (here.Applied)
        {
            Assert.Equal(["fncTotal", "spClose", "spNew", "vwOld"], here.Applied.Select(request => (string?)request["Name"]));
            Assert.Equal(["Define", "Define", "Define", "Drop"], here.Applied.Select(request => (string?)request["Action"]));
            Assert.All(here.Applied, request => Assert.Equal((id, "admin"), ((string?)request["Package"], (string?)request["By"])));
            // A drop carries no text of its own: the name is all there is to it.
            Assert.Null(here.Applied[3]["Script"]);
        }
        Assert.False(here.Objects.ContainsKey("vwOld"));
        // What was there before is kept with the result.
        var after = JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{id}/items/2"));
        Assert.Equal("CREATE PROCEDURE dbo.spClose AS RETURN", (string?)after["previous"]);
        Assert.Equal(["received", "approved", "applying", "applied"], applied["history"]!.Select(entry => (string?)entry["what"]));
        // Done is done: it is not approved again.
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new { }), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task What_a_package_did_to_the_database_is_put_back_the_last_item_first()
    {
        var (_, made) = await Made();
        var here = new PretendDatabase();
        here.Objects["spClose"] = ("Procedure", "CREATE PROCEDURE dbo.spClose AS RETURN", []);
        here.Objects["fncTotal"] = ("Function", "CREATE FUNCTION dbo.fncTotal() RETURNS int AS BEGIN RETURN 99 END", []);
        here.Objects["vwOld"] = ("View", "CREATE VIEW dbo.vwOld AS SELECT 1 AS a", []);
        here.Objects["Rates"] = ("Table", "CREATE TABLE dbo.Rates (Id int)", []);
        await using var _ = await Serve(here);
        var id = (string)(await Import(FromElsewhere(made)))["package"]!["id"]!;
        // Not applied, there is nothing to put back.
        await Json(await Api.PostAsync($"api/transport/packages/{id}/revert", null), HttpStatusCode.Conflict);
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new { }));
        await WaitFor(id, "Applied");
        // Somebody changed one of them after the package: going back loses that, and it is said.
        here.Objects["spClose"] = ("Procedure", "CREATE PROCEDURE dbo.spClose AS SELECT 'changed after'", []);
        lock (here.Applied)
            here.Applied.Clear();

        var steps = JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{id}/revert"))["steps"]!;
        Assert.Equal([(4, "can", "recreate"), (3, "can", "drop"), (2, "changed", "previous"), (1, "can", "previous")],
            steps.Select(step => ((int)step["number"]!, (string?)step["state"], (string?)step["how"])));

        Assert.Equal("Reverting", (string?)(await Json(await Api.PostAsync($"api/transport/packages/{id}/revert", null)))["package"]!["status"]);
        var back = await WaitFor(id, "Reverted", "RevertedPartly", "Applied");

        Assert.Equal(("Reverted", "admin"), ((string?)back["package"]!["status"], (string?)back["package"]!["revertBy"]));
        Assert.All(back["results"]!, result => Assert.Equal("reverted", (string?)result["reverted"]));
        lock (here.Applied)
        {
            Assert.Equal(["vwOld", "spNew", "spClose", "fncTotal"], here.Applied.Select(request => (string?)request["Name"]));
            Assert.Equal(["Define", "Drop", "Define", "Define"], here.Applied.Select(request => (string?)request["Action"]));
        }
        Assert.Equal("CREATE VIEW dbo.vwOld AS SELECT 1 AS a", here.Objects["vwOld"].Script);
        Assert.False(here.Objects.ContainsKey("spNew"));
        Assert.Equal("CREATE PROCEDURE dbo.spClose AS RETURN", here.Objects["spClose"].Script);
        Assert.EndsWith("RETURN 99 END", here.Objects["fncTotal"].Script);
        Assert.Equal(["reverting", "reverted"], back["history"]!.TakeLast(2).Select(entry => (string?)entry["what"]));
        // Put back, it waits again as it did when it arrived.
        await Json(await Api.PostAsync($"api/transport/packages/{id}/revert", null), HttpStatusCode.Conflict);
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new { at = DateTimeOffset.UtcNow.AddHours(3) }));
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/reject", new { }));
    }

    [Fact]
    public async Task A_new_version_carries_everything_and_a_script_that_already_ran_does_not_run_again()
    {
        var origin = new PretendDatabase();
        origin.Objects["Orders"] = ("Table", "CREATE TABLE dbo.Orders (Id int)", []);
        origin.Objects["spClose"] = ("Procedure", "CREATE PROCEDURE dbo.spClose AS SELECT 1", []);
        byte[] first, second;
        await using (await Serve(origin))
        {
            await ClearArea();
            await Json(await Api.PostAsJsonAsync("api/transport/area/scripts", new { draft = _draft, title = "Coluna", script = "ALTER TABLE dbo.Orders ADD Channel int" }), HttpStatusCode.Created);
            await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { draft = _draft, kind = "Procedure", schema = "dbo", name = "spClose" }), HttpStatusCode.Created);
            var v1 = await Json(await Api.PostAsJsonAsync("api/transport/packages", new { draft = _draft, name = "Pedidos" }), HttpStatusCode.Created);
            var v1Id = (string)v1["package"]!["id"]!;
            Assert.Equal(1, (int)v1["package"]!["version"]!);
            first = await Released(v1Id);

            // The next version opens with everything the first carried; something new goes in it.
            var draft = await Json(await Api.PostAsync($"api/transport/packages/{v1Id}/versions", null), HttpStatusCode.Created);
            Assert.Equal((2, "Pedidos"), ((int)draft["version"]!, (string?)draft["name"]));
            await Json(await Api.PostAsync($"api/transport/packages/{v1Id}/versions", null), HttpStatusCode.Conflict);
            _draft = (string?)draft["id"];
            Assert.Equal(2, JObject.Parse(await Api.GetStringAsync($"api/transport/area?draft={_draft}"))["items"]!.Count());
            origin.Objects["spClose"] = ("Procedure", "CREATE PROCEDURE dbo.spClose AS SELECT 2", []);
            origin.Objects["spNew"] = ("Procedure", "CREATE PROCEDURE dbo.spNew AS RETURN", []);
            await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { draft = _draft, kind = "Procedure", schema = "dbo", name = "spNew" }), HttpStatusCode.Created);
            var v2 = await Json(await Api.PostAsJsonAsync("api/transport/packages", new { draft = _draft }), HttpStatusCode.Created);
            Assert.Equal((2, (string?)v1["package"]!["line"], "Pedidos"), ((int)v2["package"]!["version"]!, (string?)v2["package"]!["line"], (string?)v2["package"]!["name"]));
            Assert.Equal(3, v2["items"]!.Count());
            second = await Released((string)v2["package"]!["id"]!);
            // The older version is not applied nor sent any more where the newer one is.
            var older = await Json(await Api.PostAsJsonAsync($"api/transport/packages/{v1Id}/approve", new { }), HttpStatusCode.Conflict);
            Assert.Contains("versão 2", (string?)older["error"]);
        }

        // Where the first version was applied, the second does not run its script again.
        var qas = new PretendDatabase();
        qas.Objects["Orders"] = ("Table", "CREATE TABLE dbo.Orders (Id int)", []);
        await using (await Serve(qas))
        {
            var one = (string)(await Import(At("QAS", first)))["package"]!["id"]!;
            await Json(await Api.PostAsJsonAsync($"api/transport/packages/{one}/approve", new { }));
            await WaitFor(one, "Applied");
            var two = (string)(await Import(At("QAS", second)))["package"]!["id"]!;
            var checks = JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{two}/check"))["checks"]!;
            Assert.Equal("ran", (string?)checks.First(check => (int)check["number"]! == 1)["state"]);
            await Json(await Api.PostAsJsonAsync($"api/transport/packages/{two}/approve", new { }));
            var applied = await WaitFor(two, "Applied");
            Assert.Equal("skipped", (string?)applied["results"]![0]!["did"]);
            lock (qas.Applied)
                Assert.Equal(["Script", "Define", "Define", "Define"], qas.Applied.Select(request => (string?)request["Action"]));
        }

        // Where only the newest arrives, everything in it runs, the script once.
        var prd = new PretendDatabase();
        prd.Objects["Orders"] = ("Table", "CREATE TABLE dbo.Orders (Id int)", []);
        await using (await Serve(prd))
        {
            var two = (string)(await Import(At("PRD", second)))["package"]!["id"]!;
            await Json(await Api.PostAsJsonAsync($"api/transport/packages/{two}/approve", new { }));
            var applied = await WaitFor(two, "Applied");
            Assert.Equal("Applied", (string?)applied["package"]!["status"]);
            lock (prd.Applied)
                Assert.Equal(["Script", "Define", "Define"], prd.Applied.Select(request => (string?)request["Action"]));
        }

        // An older version waiting where a newer one arrives gives it its place.
        var other = new PretendDatabase();
        other.Objects["Orders"] = ("Table", "CREATE TABLE dbo.Orders (Id int)", []);
        await using (await Serve(other))
        {
            var one = (string)(await Import(At("OTHER", first)))["package"]!["id"]!;
            await Import(At("OTHER", second));
            Assert.Equal("Superseded", (string?)JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{one}"))["package"]!["status"]);
            await Json(await Api.PostAsJsonAsync($"api/transport/packages/{one}/approve", new { }), HttpStatusCode.Conflict);
        }
    }

    [Fact]
    public async Task The_rules_of_the_environment_say_what_comes_by_hand_who_approves_and_when()
    {
        var policy = server.Services.GetRequiredService<global::ZapMQ.Server.Transport.TransportPolicyStore>();
        var origin = new PretendDatabase();
        origin.Objects["spClose"] = ("Procedure", "CREATE PROCEDURE dbo.spClose AS RETURN", []);
        await using var _ = await Serve(origin);
        await ClearArea();
        try
        {
            // Only by transport: nothing comes in by hand, and only the master changes that.
            await Json(await Api.PutAsJsonAsync("api/transport/policy", new { manualVersions = false }));
            Assert.False((bool)JObject.Parse(await Api.GetStringAsync("api/transport/policy"))["manualVersions"]!);
            var refused = await Json(await Api.PostAsJsonAsync("api/transport/area/scripts", new { draft = _draft, title = "x", script = "SELECT 1" }), HttpStatusCode.Forbidden);
            Assert.Contains("só por transporte", (string?)refused["error"]);
            await Json(await Api.PostAsync($"api/transport/area/upload?draft={_draft}&kind=api&name=Orders", new ByteArrayContent([1, 2])), HttpStatusCode.Forbidden);
            await Json(await Api.PostAsync("api/transport/applications/files", new ByteArrayContent([1, 2])), HttpStatusCode.Forbidden);
            // What is already here still goes in a package.
            await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { draft = _draft, kind = "Procedure", schema = "dbo", name = "spClose" }), HttpStatusCode.Created);
            var made = (string)(await Json(await Api.PostAsJsonAsync("api/transport/packages", new { draft = _draft, name = "Regras" }), HttpStatusCode.Created))["package"]!["id"]!;

            // Whoever made it does not approve it; and packages go in only within the hours.
            await Json(await Api.PutAsJsonAsync("api/transport/policy", new { manualVersions = true, separateApprover = true }));
            Assert.Contains("não o aprova", (string?)(await Json(await Api.PostAsJsonAsync($"api/transport/packages/{made}/approve", new { }), HttpStatusCode.Forbidden))["error"]);
            var now = DateTimeOffset.Now;
            await Json(await Api.PutAsJsonAsync("api/transport/policy", new { manualVersions = true, windowStart = now.AddHours(2).ToString("HH:00"), windowEnd = now.AddHours(3).ToString("HH:00") }));
            Assert.Contains("só entre", (string?)(await Json(await Api.PostAsJsonAsync($"api/transport/packages/{made}/approve", new { }), HttpStatusCode.Conflict))["error"]);
            var inside = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 30, 0, now.Offset).AddHours(2);
            await Json(await Api.PostAsJsonAsync($"api/transport/packages/{made}/approve", new { at = inside }));
            await Json(await Api.PutAsJsonAsync("api/transport/policy", new { windowStart = "22:00" }), HttpStatusCode.BadRequest);
        }
        finally
        {
            policy.Set(new() { ManualVersions = true }, "test", DateTimeOffset.UtcNow);
        }
    }

    [Fact]
    public async Task Where_packages_are_not_made_nothing_makes_one_and_versions_by_hand_go_off_with_it()
    {
        var policy = server.Services.GetRequiredService<global::ZapMQ.Server.Transport.TransportPolicyStore>();
        await ClearArea();
        try
        {
            await Json(await Api.PutAsJsonAsync("api/transport/policy", new { createPackages = false, manualVersions = true }));
            var rules = JObject.Parse(await Api.GetStringAsync("api/transport/policy"));
            Assert.Equal((false, false), ((bool)rules["createPackages"]!, (bool)rules["manualVersions"]!));
            Assert.Contains("não se criam pacotes", (string?)(await Json(await Api.PostAsJsonAsync("api/transport/drafts", new { name = "Novo" }), HttpStatusCode.Forbidden))["error"]);
            // Nor is anything put in, or closed, in a package that was open before.
            await Json(await Api.PostAsJsonAsync("api/transport/area/scripts", new { draft = _draft, title = "x", script = "SELECT 1" }), HttpStatusCode.Forbidden);
            await Json(await Api.PostAsync($"api/transport/area/upload?draft={_draft}&kind=api&name=Orders", new ByteArrayContent([1, 2])), HttpStatusCode.Forbidden);
            await Json(await Api.PostAsJsonAsync("api/transport/packages", new { draft = _draft, name = "Fechar" }), HttpStatusCode.Forbidden);
        }
        finally
        {
            policy.Set(new() { ManualVersions = true }, "test", DateTimeOffset.UtcNow);
        }
    }

    [Fact]
    public async Task A_package_goes_on_only_when_everybody_who_put_items_in_it_released_them()
    {
        await using var _ = await Serve(new PretendDatabase());
        await ClearArea();
        await Api.PostAsJsonAsync("api/users", new { login = "bira.libera", name = "Bira", password = "senha-do-bira" });
        using var bira = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = Api.BaseAddress };
        Assert.Equal(HttpStatusCode.OK, (await bira.PostAsJsonAsync("api/login", new { user = "bira.libera", password = "senha-do-bira" })).StatusCode);

        await Json(await Api.PostAsJsonAsync("api/transport/area/scripts", new { draft = _draft, title = "Do admin", script = "SELECT 1" }), HttpStatusCode.Created);
        await Json(await bira.PostAsJsonAsync("api/transport/area/scripts", new { draft = _draft, title = "Do Bira 1", script = "SELECT 2" }), HttpStatusCode.Created);
        await Json(await bira.PostAsJsonAsync("api/transport/area/scripts", new { draft = _draft, title = "Do Bira 2", script = "SELECT 3" }), HttpStatusCode.Created);

        // Said while it is open, and taken back when what one put in changes.
        await Json(await Api.PostAsJsonAsync($"api/transport/drafts/{_draft}/release", new { }));
        await Json(await bira.PostAsJsonAsync($"api/transport/drafts/{_draft}/release", new { }));
        var other = await Json(await Api.PostAsJsonAsync("api/transport/area/scripts", new { draft = _draft, title = "Mais um", script = "SELECT 4" }), HttpStatusCode.Created);
        var open = JObject.Parse(await Api.GetStringAsync("api/transport/drafts"))["drafts"]!.Single(draft => (string?)draft["id"] == _draft)!;
        Assert.Equal(["admin", "bira.libera"], open["contributors"]!.Select(user => (string?)user));
        Assert.Equal(["bira.libera"], open["releases"]!.Select(release => (string?)release["user"]));
        await Api.DeleteAsync($"api/transport/area/{(string?)other["id"]}");
        // Each one says it for their own; only somebody who has items says it at all.
        await Json(await bira.PostAsJsonAsync($"api/transport/drafts/{_draft}/release", new { user = "admin" }), HttpStatusCode.Forbidden);
        await Json(await Api.PostAsJsonAsync($"api/transport/drafts/{_draft}/release", new { user = "ninguem" }), HttpStatusCode.BadRequest);

        var closed = await Json(await Api.PostAsJsonAsync("api/transport/packages", new { draft = _draft, name = "Dois autores", keepOrder = true }), HttpStatusCode.Created);
        var id = (string)closed["package"]!["id"]!;
        Assert.Equal(["admin", "bira.libera", "bira.libera"], closed["items"]!.Select(item => (string?)item["addedBy"]));
        Assert.Equal(["admin"], closed["package"]!["waiting"]!.Select(user => (string?)user));
        var refused = await Json(await Api.GetAsync($"api/transport/packages/{id}/download"), HttpStatusCode.Conflict);
        Assert.Contains("Falta: admin", (string?)refused["error"]);

        // The master may say it for somebody who cannot, and it is written down that way.
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/withdraw", new { user = "bira.libera" }));
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/release", new { }));
        var released = await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/release", new { user = "bira.libera" }));
        Assert.Empty(released["package"]!["waiting"]!);
        Assert.Contains(released["history"]!, entry => (string?)entry["what"] == "released" && (string?)entry["detail"] == "em nome de bira.libera");
        Assert.NotEmpty(await Api.GetByteArrayAsync($"api/transport/packages/{id}/download"));
        Assert.Contains("Dois autores", JObject.Parse(await Api.GetStringAsync("api/transport/packages"))["packages"]!
            .Where(package => package["contributors"]!.Any(user => (string?)user == "bira.libera")).Select(package => (string?)package["name"]));
    }

    [Fact]
    public async Task Going_back_to_a_kept_copy_needs_a_reason_and_the_number_of_every_application_is_the_masters()
    {
        Assert.Contains("por que", (string?)(await Json(await Api.PostAsJsonAsync("api/transport/backups/restore", new { kind = "service", name = "Orders", backup = "20261022-120000-pkg", reason = " " }), HttpStatusCode.BadRequest))["error"]);
        await Api.PostAsJsonAsync("api/users", new { login = "copias.user", name = "Cópias", password = "senha-das-copias" });
        using var other = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = Api.BaseAddress };
        Assert.Equal(HttpStatusCode.OK, (await other.PostAsJsonAsync("api/login", new { user = "copias.user", password = "senha-das-copias" })).StatusCode);
        await Json(await other.PutAsJsonAsync("api/transport/backups/keep", new { value = 5 }), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_endpoints_of_an_api_say_what_they_run_and_an_object_says_which_endpoints_use_it()
    {
        var origin = new PretendDatabase();
        origin.Objects["spCadAntena"] = ("Procedure", "CREATE PROCEDURE dbo.spCadAntena AS RETURN", []);
        origin.Objects["fncLista"] = ("Function", "CREATE FUNCTION dbo.fncLista() RETURNS TABLE AS RETURN SELECT 1 AS x", []);
        origin.ApiMap = JObject.Parse("""
            { "format": 1, "application": "Cadastro", "endpoints": [
              { "method": "post", "route": "/api/cadastro/antena", "commands": [ { "sql": "EXEC spCadAntena @i_codigo = @i_codigo" } ] },
              { "method": "GET", "route": "/api/cadastro/lista", "commands": [ { "sql": "SELECT * FROM fncLista() WHERE 1 = 1 /*filtro opcional*/ AND x = @x", "dynamic": true } ] },
              { "method": "PUT", "route": "/api/cadastro/padrao", "commands": [ { "sql": "DECLARE @t typePadrao INSERT INTO @t VALUES /*valores*/ EXEC spCadPadrao @t = @t", "dynamic": true } ], "objects": [ "dbo.typePadrao" ] }
            ] }
            """);
        origin.OpenApi = JObject.Parse("""{ "openapi": "3.0.1", "paths": {} }""");
        await using var _ = await Serve(origin);
        await ClearArea();

        var described = await Json(await Api.GetAsync("api/transport/endpoints?kind=api&name=Cadastro&fresh=true"));
        Assert.Equal(("Sales", "3.0.1"), ((string?)described["Database"], (string?)described["OpenApi"]!["openapi"]));
        var endpoints = described["Map"]!["endpoints"]!;
        // Read from the SQL, and found in the catalog to be opened on their own screens.
        Assert.Equal(["dbo.spCadAntena:Procedure"], endpoints[0]!["found"]!.Select(found => $"{found["name"]}:{found["kind"]}"));
        Assert.Equal(["dbo.fncLista:Function"], endpoints[1]!["found"]!.Select(found => $"{found["name"]}:{found["kind"]}"));
        // A text made of parts is not whole T-SQL: what it runs by name is still found, and what the map names too.
        Assert.Equal(["dbo.spCadPadrao", "dbo.typePadrao"], endpoints[2]!["found"]!.Select(found => (string?)found["name"]));

        var used = await Json(await Api.GetAsync("api/transport/endpoints/used?schema=dbo&name=spCadAntena"));
        Assert.Equal(["Cadastro POST /api/cadastro/antena"], used["uses"]!.Select(use => $"{use["api"]} {use["method"]} {use["route"]}"));

        // A map that changes after the index was made is seen at once, without waiting for anything to expire.
        ((JArray)origin.ApiMap["endpoints"]!).Add(JObject.Parse("""{ "method": "DELETE", "route": "/api/cadastro/antena/{codigo}", "commands": [ { "sql": "EXEC spCadAntena @i_codigo = @i_codigo" } ] }"""));
        used = await Json(await Api.GetAsync("api/transport/endpoints/used?schema=dbo&name=spCadAntena"));
        Assert.Equal(["POST /api/cadastro/antena", "DELETE /api/cadastro/antena/{codigo}"], used["uses"]!.Select(use => $"{use["method"]} {use["route"]}"));

        // A package that changes it says, by item, the endpoints of this environment that use it.
        await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { draft = _draft, kind = "Procedure", schema = "dbo", name = "spCadAntena" }), HttpStatusCode.Created);
        var id = (string)(await Json(await Api.PostAsJsonAsync("api/transport/packages", new { draft = _draft, name = "Antena" }), HttpStatusCode.Created))["package"]!["id"]!;
        var affected = await Json(await Api.GetAsync($"api/transport/packages/{id}/endpoints"));
        Assert.Equal(["/api/cadastro/antena", "/api/cadastro/antena/{codigo}"], affected["items"]!["1"]!.Select(use => (string?)use["route"]));
    }

    [Fact]
    public async Task A_script_goes_after_the_objects_of_the_package_it_uses()
    {
        var origin = new PretendDatabase();
        origin.Objects["spLoad"] = ("Procedure", "CREATE PROCEDURE dbo.spLoad AS RETURN", []);
        origin.Objects["Orders"] = ("Table", "CREATE TABLE dbo.Orders (Id int)", []);
        await using var _ = await Serve(origin);
        await ClearArea();
        await Json(await Api.PostAsJsonAsync("api/transport/area/scripts", new { draft = _draft, title = "Carga", script = "EXEC dbo.spLoad; UPDATE dbo.Orders SET Id = 1" }), HttpStatusCode.Created);
        await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { draft = _draft, kind = "Procedure", schema = "dbo", name = "spLoad" }), HttpStatusCode.Created);
        await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { draft = _draft, kind = "Table", schema = "dbo", name = "Orders" }), HttpStatusCode.Created);
        var closed = await Json(await Api.PostAsJsonAsync("api/transport/packages", new { draft = _draft, name = "Carga" }), HttpStatusCode.Created);

        // The table first by its kind; the script, which would come before procedures, waits for the one it runs.
        Assert.Equal(["Orders", "spLoad", null], closed["items"]!.Select(item => (string?)item["name"]));
    }

    [Fact]
    public async Task A_script_that_names_what_is_neither_here_nor_before_it_in_the_package_is_not_approved()
    {
        var origin = new PretendDatabase();
        byte[] made;
        await using (await Serve(origin))
        {
            await ClearArea();
            await Json(await Api.PostAsJsonAsync("api/transport/area/scripts", new { draft = _draft, title = "Tabela", script = "CREATE TABLE dbo.Fresh (Id int)" }), HttpStatusCode.Created);
            await Json(await Api.PostAsJsonAsync("api/transport/area/scripts", new { draft = _draft, title = "Carga", script = "INSERT INTO dbo.Fresh (Id) SELECT p.Id FROM dbo.Prices p JOIN dbo.Lost l ON l.Id = p.Id" }), HttpStatusCode.Created);
            var closed = await Json(await Api.PostAsJsonAsync("api/transport/packages", new { draft = _draft, name = "Carga" }), HttpStatusCode.Created);
            made = await Released((string?)closed["package"]!["id"]);
        }

        var here = new PretendDatabase();
        here.Objects["Prices"] = ("Table", "CREATE TABLE dbo.Prices (Id int)", []);
        await using var _ = await Serve(here);
        var id = (string)(await Import(FromElsewhere(made)))["package"]!["id"]!;

        // The table the first script makes is not missing for the second; the one nobody makes is.
        var checks = JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{id}/check"))["checks"]!;
        Assert.Empty(checks[0]!["missing"]!);
        Assert.Equal(["dbo.Lost"], checks[1]!["missing"]!.Select(name => (string?)name));
        var refused = await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new { }), HttpStatusCode.Conflict);
        Assert.Contains("dbo.Lost", (string?)refused["error"]);

        here.Objects["Lost"] = ("Table", "CREATE TABLE dbo.Lost (Id int)", []);
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new { }));
        await WaitFor(id, "Applied");
    }

    [Fact]
    public async Task Putting_back_stops_at_what_fails_and_leaves_alone_what_cannot_be_undone()
    {
        var origin = new PretendDatabase();
        origin.Objects["spClose"] = ("Procedure", "CREATE PROCEDURE dbo.spClose AS SELECT 2", []);
        origin.Objects["Rates"] = ("Table", "CREATE TABLE dbo.Rates (Id int)", []);
        byte[] made;
        await using (await Serve(origin))
        {
            await ClearArea();
            await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { draft = _draft, kind = "Procedure", schema = "dbo", name = "spClose" }), HttpStatusCode.Created);
            await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { draft = _draft, kind = "Table", schema = "dbo", name = "Rates" }), HttpStatusCode.Created);
            await Json(await Api.PostAsJsonAsync("api/transport/area/scripts", new { draft = _draft, title = "Fill", script = "UPDATE dbo.Rates SET Id = 1" }), HttpStatusCode.Created);
            var closed = await Json(await Api.PostAsJsonAsync("api/transport/packages", new { draft = _draft, name = "Rates" }), HttpStatusCode.Created);
            made = await Released((string?)closed["package"]!["id"]);
        }

        var here = new PretendDatabase();
        here.Objects["spClose"] = ("Procedure", "CREATE PROCEDURE dbo.spClose AS SELECT 1", []);
        await using var _ = await Serve(here);
        var id = (string)(await Import(FromElsewhere(made)))["package"]!["id"]!;
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new { }));
        await WaitFor(id, "Applied");

        // The table and the script stay; only the procedure can go back.
        var steps = JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{id}/revert"))["steps"]!;
        Assert.Equal(["can", "cannot", "cannot"], steps.Select(step => (string?)step["state"]));

        here.OnApply = _ => new JObject { ["Ok"] = true, ["Applied"] = false, ["Problem"] = "Deadlocked", ["Messages"] = new JArray() };
        await Json(await Api.PostAsync($"api/transport/packages/{id}/revert", null));
        var failed = await WaitFor(id, "Applied", "RevertedPartly", "Reverted");
        // Nothing went back: it is as applied as it was, and it says why.
        Assert.Equal("Applied", (string?)failed["package"]!["status"]);
        Assert.Equal(("failed", "Deadlocked"), ((string?)failed["results"]![2]!["reverted"], (string?)failed["results"]![2]!["revertProblem"]));
        Assert.Equal("revert-failed", (string?)failed["history"]!.Last()["what"]);

        here.OnApply = null;
        await Json(await Api.PostAsync($"api/transport/packages/{id}/revert", null));
        var partly = await WaitFor(id, "RevertedPartly", "Reverted");
        Assert.Equal("RevertedPartly", (string?)partly["package"]!["status"]);
        Assert.Equal([null, null, "reverted"], partly["results"]!.Select(result => (string?)result["reverted"]));
        Assert.Equal("CREATE PROCEDURE dbo.spClose AS SELECT 1", here.Objects["spClose"].Script);
        // What is left cannot be put back from here.
        await Json(await Api.PostAsync($"api/transport/packages/{id}/revert", null), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Files_go_back_from_the_copy_kept_and_not_under_a_package_applied_after()
    {
        var dev = new PretendDatabase();
        dev.Targets["api:Billing"] = new() { ["Billing.dll"] = "v1" };
        await using var _ = await Serve(dev);
        await ClearArea();

        async Task<string> Applied(string version)
        {
            await ClearArea();
            dev.Incoming["api:Billing"] = new() { ["Billing.dll"] = version };
            await Json(await Api.PostAsJsonAsync("api/transport/area/incoming", new { draft = _draft, kind = "api", name = "Billing" }), HttpStatusCode.Created);
            var made = (string)(await Json(await Api.PostAsJsonAsync("api/transport/packages", new { draft = _draft, name = "Billing " + version }), HttpStatusCode.Created))["package"]!["id"]!;
            await Json(await Api.PostAsJsonAsync($"api/transport/packages/{made}/approve", new { }));
            await WaitFor(made, "Applied");
            return made;
        }
        var first = await Applied("v2");
        var second = await Applied("v3");

        var blocked = JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{first}/revert"))["steps"]!.Single();
        Assert.Equal("cannot", (string?)blocked["state"]);
        Assert.Contains("Billing v3", (string?)blocked["reason"]);
        await Json(await Api.PostAsync($"api/transport/packages/{first}/revert", null), HttpStatusCode.Conflict);

        Assert.Equal("can", (string?)JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{second}/revert"))["steps"]!.Single()["state"]);
        await Json(await Api.PostAsync($"api/transport/packages/{second}/revert", null));
        var back = await WaitFor(second, "Reverted", "RevertedPartly", "Applied");
        Assert.Equal(("Reverted", "reverted", "/backup/before-going-back"), ((string?)back["package"]!["status"], (string?)back["results"]![0]!["reverted"], (string?)back["results"]![0]!["revertBackup"]));
        lock (dev.Reverted)
            Assert.Equal((second, 1, "api", "Billing", "/backup/there", "admin"), ((string?)dev.Reverted.Single()["Package"], (int)dev.Reverted[0]["Item"]!, (string?)dev.Reverted[0]["Kind"], (string?)dev.Reverted[0]["Name"], (string?)dev.Reverted[0]["Backup"], (string?)dev.Reverted[0]["By"]));

        // With the later one out of the way, the first can go back too.
        dev.Targets["api:Billing"] = new() { ["Billing.dll"] = "v2" };
        Assert.Equal("can", (string?)JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{first}/revert"))["steps"]!.Single()["state"]);
        await Json(await Api.PostAsync($"api/transport/packages/{first}/revert", null));
        await WaitFor(first, "Reverted");
    }

    [Fact]
    public async Task Applying_stops_at_the_first_item_that_fails_and_nothing_is_tried_again()
    {
        var (_, made) = await Made();
        var here = new PretendDatabase
        {
            OnApply = request => (string?)request["Name"] == "spClose"
                ? new JObject { ["Ok"] = true, ["Applied"] = false, ["Problem"] = "Invalid object name 'dbo.Rates'.", ["Batch"] = 1, ["Line"] = 3, ["Messages"] = new JArray() }
                : null
        };
        here.Objects["Rates"] = ("Table", "CREATE TABLE dbo.Rates (Id int)", []);
        await using var _ = await Serve(here);
        var id = (string)(await Import(FromElsewhere(made)))["package"]!["id"]!;

        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new { }));
        var paused = await WaitFor(id, "Paused", "Applied", "Partial", "Failed");
        Assert.Equal("Paused", (string?)paused["package"]!["status"]);
        Assert.True((int)JObject.Parse(await Api.GetStringAsync("api/transport/summary"))["troubled"]! > 0);
        var ended = await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/decide", new { proceed = false }));

        Assert.Equal("Partial", (string?)ended["package"]!["status"]);
        Assert.Equal(["applied", "failed"], ended["results"]!.Select(result => (string?)result["status"]));
        Assert.Equal(("Invalid object name 'dbo.Rates'.", 3), ((string?)ended["results"]![1]!["problem"], (int)ended["results"]![1]!["line"]!));
        await Task.Delay(500);
        lock (here.Applied)
            Assert.Equal(2, here.Applied.Count);
        var troubled = (int)JObject.Parse(await Api.GetStringAsync("api/transport/summary"))["troubled"]!;
        Assert.True(troubled > 0);
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/decide", new { proceed = false }), HttpStatusCode.Conflict);
        // Somebody knows about it: it stays as it is, and stops asking for attention.
        var known = await Json(await Api.PostAsync($"api/transport/packages/{id}/acknowledge", null));
        Assert.Equal(("Partial", "admin", "acknowledged"), ((string?)known["package"]!["status"], (string?)known["package"]!["acknowledgedBy"], (string?)known["history"]!.Last()["what"]));
        Assert.Equal(troubled - 1, (int)JObject.Parse(await Api.GetStringAsync("api/transport/summary"))["troubled"]!);
    }

    [Fact]
    public async Task A_package_that_stopped_at_an_item_that_failed_goes_on_with_the_others_only_when_somebody_says_so()
    {
        var (_, made) = await Made();
        var here = new PretendDatabase
        {
            OnApply = request => (string?)request["Name"] == "spClose"
                ? new JObject { ["Ok"] = true, ["Applied"] = false, ["Problem"] = "Invalid object name 'dbo.Rates'.", ["Batch"] = 1, ["Line"] = 3, ["Messages"] = new JArray() }
                : null
        };
        here.Objects["Rates"] = ("Table", "CREATE TABLE dbo.Rates (Id int)", []);
        await using var _ = await Serve(here);
        var id = (string)(await Import(FromElsewhere(made)))["package"]!["id"]!;

        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new { }));
        var paused = await WaitFor(id, "Paused", "Applied", "Partial", "Failed");
        Assert.Equal("Paused", (string?)paused["package"]!["status"]);
        Assert.Equal(["applied", "failed"], paused["results"]!.Select(result => (string?)result["status"]));
        // Nothing moves on its own while the question is open.
        await Task.Delay(400);
        lock (here.Applied)
            Assert.Equal(2, here.Applied.Count);

        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/decide", new { proceed = true }));
        var ended = await WaitFor(id, "Applied", "Partial", "Failed");

        // The items that were done are not done again and the one that failed is not tried again.
        Assert.Equal("Partial", (string?)ended["package"]!["status"]);
        Assert.Equal(["applied", "failed", "applied", "applied"], ended["results"]!.Select(result => (string?)result["status"]));
        Assert.Equal([1, 2, 3, 4], ended["results"]!.Select(result => (int)result["number"]!));
        lock (here.Applied)
            Assert.Equal(4, here.Applied.Count);
        Assert.Contains(ended["history"]!, entry => (string?)entry["what"] == "continued");
    }

    [Fact]
    public async Task What_changed_here_beside_what_is_being_carried_is_told_before_the_package_is_closed_or_sent()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var (shell, brief, adm) = ("shell" + tag, "brief" + tag, "adm" + tag);
        var here = new PretendDatabase();
        foreach (var module in new[] { shell, brief, adm })
            here.Targets["frontend:" + module] = new() { ["remoteEntry.json"] = "v1" };
        // What other tests left applied in the same panel is not of interest here.
        IEnumerable<string?> Ours(JObject answer) => answer["items"]!.Select(item => (string?)item["name"]).Where(name => name!.EndsWith(tag)).Order();
        await using var _ = await Serve(here);
        await ClearArea();

        // Three modules brought by hand, each applied in a package of its own.
        var made = new List<string>();
        foreach (var module in new[] { shell, brief, adm })
        {
            var draft = (string?)(await Json(await Api.PostAsJsonAsync("api/transport/drafts", new { name = module }), HttpStatusCode.Created))["id"];
            await Json(await Api.PostAsync($"api/transport/area/upload?draft={draft}&kind=frontend&name={module}", new ByteArrayContent(PretendDatabase.Zipped(new() { ["remoteEntry.json"] = "{" + module + "}" }))), HttpStatusCode.Created);
            var id = (string)(await Json(await Api.PostAsJsonAsync("api/transport/packages", new { draft }), HttpStatusCode.Created))["package"]!["id"]!;
            await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/release", new { }));
            await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new { }));
            await WaitFor(id, "Applied");
            made.Add(id);
        }

        // A new package with only one of them: the other two are told.
        await ClearArea();
        await Json(await Api.PostAsync($"api/transport/area/upload?draft={_draft}&kind=frontend&name={shell}", new ByteArrayContent(PretendDatabase.Zipped(new() { ["remoteEntry.json"] = "{shell2}" }))), HttpStatusCode.Created);
        var told = JObject.Parse(await Api.GetStringAsync($"api/transport/drafts/{_draft}/forgotten"));
        Assert.Equal([adm, brief], Ours(told));

        // A package that is already closed says the same about what it does not carry.
        Assert.Equal([adm, brief], Ours(JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{made[0]}/forgotten"))));

        // With the three in it, nothing is left out.
        foreach (var module in new[] { brief, adm })
            await Json(await Api.PostAsync($"api/transport/area/upload?draft={_draft}&kind=frontend&name={module}", new ByteArrayContent(PretendDatabase.Zipped(new() { ["remoteEntry.json"] = "{x}" }))), HttpStatusCode.Created);
        Assert.Empty(Ours(JObject.Parse(await Api.GetStringAsync($"api/transport/drafts/{_draft}/forgotten"))));

        // Another kind of thing is not asked about: an API goes without the modules.
        await ClearArea();
        await Json(await Api.PostAsync($"api/transport/area/upload?draft={_draft}&kind=api&name=Orders{tag}", new ByteArrayContent(PretendDatabase.Zipped(new() { ["Orders.dll"] = "x" }))), HttpStatusCode.Created);
        Assert.Empty(Ours(JObject.Parse(await Api.GetStringAsync($"api/transport/drafts/{_draft}/forgotten"))));
    }

    [Fact]
    public async Task An_approval_may_be_for_later_and_may_be_taken_back_and_a_package_may_be_refused()
    {
        var (_, made) = await Made();
        var here = new PretendDatabase();
        here.Objects["Rates"] = ("Table", "CREATE TABLE dbo.Rates (Id int)", []);
        await using var _ = await Serve(here);
        var id = (string)(await Import(FromElsewhere(made)))["package"]!["id"]!;

        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new { at = DateTimeOffset.UtcNow.AddHours(-2) }), HttpStatusCode.BadRequest);
        var scheduled = await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new { at = DateTimeOffset.UtcNow.AddHours(3) }));
        Assert.Equal("Approved", (string?)scheduled["package"]!["status"]);
        await Task.Delay(700);
        lock (here.Applied)
            Assert.Empty(here.Applied);

        // Taken back, it waits again; refused, it is over.
        Assert.Equal("Pending", (string?)(await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/reject", new { })))["package"]!["status"]);
        var refused = await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/reject", new { reason = "Falta o script da tabela" }));
        Assert.Equal("Rejected", (string?)refused["package"]!["status"]);
        Assert.Equal("Falta o script da tabela", (string?)refused["history"]!.Last()["detail"]);
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new { }), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_file_that_is_not_a_whole_package_is_not_taken_in()
    {
        var (_, made) = await Made();

        await Import(Encoding.UTF8.GetBytes("not a zip at all"), HttpStatusCode.BadRequest);
        await Import([], HttpStatusCode.BadRequest);
        // A script that was changed on the way no longer is what the package says it carries.
        var tampered = await Import(FromElsewhere(made, text => text + "\nDROP TABLE dbo.Orders"), HttpStatusCode.BadRequest);
        Assert.Contains("alterado", (string?)tampered["error"]);
        using var anonymous = new HttpClient { BaseAddress = new Uri($"http://localhost:{server.PanelPort}/") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("api/transport/packages/import", new ByteArrayContent(made))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("api/transport/packages")).StatusCode);
    }

    [Fact]
    public async Task An_item_that_names_a_database_this_environment_does_not_have_is_not_taken_for_new()
    {
        // Made where there are two databases, so the items say which one.
        var origin = new PretendDatabase { Databases = ["Sales", "Stock"] };
        origin.Objects["spNew"] = ("Procedure", "CREATE PROCEDURE dbo.spNew AS RETURN", []);
        byte[] made;
        await using (await Serve(origin))
        {
            await ClearArea();
            Assert.Equal("Sales", (string?)(await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { draft = _draft, database = "Sales", kind = "Procedure", schema = "dbo", name = "spNew" }), HttpStatusCode.Created))["database"]);
            var closed = await Json(await Api.PostAsJsonAsync("api/transport/packages", new { draft = _draft, name = "Two databases" }), HttpStatusCode.Created);
            made = await Released((string?)closed["package"]!["id"]);
        }
        var here = new PretendDatabase { Databases = ["Other"] };
        await using var _ = await Serve(here);
        var id = (string)(await Import(FromElsewhere(made)))["package"]!["id"]!;

        var check = JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{id}/check"))["checks"]![0]!;

        Assert.Equal("unknown", (string?)check["state"]);
        Assert.Contains("não acompanha o banco", (string?)check["problem"]);
    }

    [Fact]
    public async Task A_package_that_brings_a_table_this_environment_already_has_is_not_approved()
    {
        var origin = new PretendDatabase();
        origin.Objects["Orders"] = ("Table", "CREATE TABLE dbo.Orders (Id int, Channel varchar(10))", []);
        byte[] made;
        await using (await Serve(origin))
        {
            await ClearArea();
            await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { draft = _draft, kind = "Table", schema = "dbo", name = "Orders" }), HttpStatusCode.Created);
            var closed = await Json(await Api.PostAsJsonAsync("api/transport/packages", new { draft = _draft, name = "A table" }), HttpStatusCode.Created);
            made = await Released((string?)closed["package"]!["id"]);
        }

        // Where the table is as the package brings it, there is nothing in the way; where it is another, there is.
        var same = new PretendDatabase();
        same.Objects["Orders"] = origin.Objects["Orders"];
        await using (await Serve(same))
        {
            var id = (string)(await Import(FromElsewhere(made)))["package"]!["id"]!;
            Assert.Equal("same", (string?)JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{id}/check"))["checks"]![0]!["state"]);
        }
        var here = new PretendDatabase();
        here.Objects["Orders"] = ("Table", "CREATE TABLE dbo.Orders (Id int)", []);
        await using var _ = await Serve(here);
        var blocked = (string)(await Import(FromElsewhere(made)))["package"]!["id"]!;

        Assert.Equal("blocked", (string?)JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{blocked}/check"))["checks"]![0]!["state"]);
        var refused = await Json(await Api.PostAsJsonAsync($"api/transport/packages/{blocked}/approve", new { }), HttpStatusCode.Conflict);
        Assert.Contains("script de alteração", (string?)refused["error"]);
        lock (here.Applied)
            Assert.Empty(here.Applied);
    }

    [Fact]
    public async Task What_was_made_here_can_be_deleted_and_what_arrived_can_only_be_refused()
    {
        var (_, made) = await Made();
        var mine = (string)JObject.Parse(await Api.GetStringAsync("api/transport/packages"))["packages"]!.First(package => !(bool)package["received"]!)["id"]!;
        var here = new PretendDatabase();
        await using var _ = await Serve(here);
        var arrived = (string)(await Import(FromElsewhere(made)))["package"]!["id"]!;

        // Made here: it goes away, file and all, and is not refused. What it carried is in the area again.
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{mine}/reject", new { }), HttpStatusCode.Conflict);
        Assert.Empty(JObject.Parse(await Api.GetStringAsync("api/transport/area"))["items"]!);
        Assert.Equal(HttpStatusCode.NoContent, (await Api.DeleteAsync($"api/transport/packages/{mine}")).StatusCode);
        var back = JObject.Parse(await Api.GetStringAsync("api/transport/area"))["items"]!;
        Assert.Equal(["fncTotal|Define", "spClose|Define", "spNew|Define", "vwOld|Drop"], back.Select(item => $"{(string?)item["name"]}|{(string?)item["action"]}").Order());
        Assert.All(back, item => Assert.Equal("admin", (string?)item["addedBy"]));
        Assert.Equal(HttpStatusCode.NotFound, (await Api.GetAsync($"api/transport/packages/{mine}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.GetAsync($"api/transport/packages/{mine}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.DeleteAsync($"api/transport/packages/{mine}")).StatusCode);
        // What it carried counts as not carried again.
        Assert.False(JObject.Parse(await Api.GetStringAsync("api/transport/packaged")).ContainsKey("procedure|dbo|spnew"));

        // Arrived: it stays, refused or not, with what it brought.
        Assert.Equal(HttpStatusCode.Conflict, (await Api.DeleteAsync($"api/transport/packages/{arrived}")).StatusCode);
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{arrived}/reject", new { reason = "Não vai entrar" }));
        Assert.Equal(HttpStatusCode.Conflict, (await Api.DeleteAsync($"api/transport/packages/{arrived}")).StatusCode);
        var kept = JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{arrived}"));
        Assert.Equal(("Rejected", 4), ((string?)kept["package"]!["status"], kept["items"]!.Count()));
        Assert.Contains("fncTotal", (string?)JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{arrived}/items/1"))["script"]);
        lock (here.Applied)
            Assert.Empty(here.Applied);
    }

    [Fact]
    public async Task A_script_comes_back_to_the_area_with_its_text_and_a_delivered_package_is_not_deleted()
    {
        var database = new PretendDatabase();
        database.Objects["spNew"] = ("Procedure", "CREATE PROCEDURE dbo.spNew AS RETURN", []);
        await using var _ = await Serve(database);
        await ClearArea();
        await Json(await Api.PostAsJsonAsync("api/transport/area/scripts", new { draft = _draft, title = "Nova coluna", script = "ALTER TABLE dbo.Orders ADD Channel varchar(10) NULL" }), HttpStatusCode.Created);
        await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { draft = _draft, kind = "Procedure", schema = "dbo", name = "spNew" }), HttpStatusCode.Created);
        var first = (string)(await Json(await Api.PostAsJsonAsync("api/transport/packages", new { draft = _draft, name = "To be deleted" }), HttpStatusCode.Created))["package"]!["id"]!;

        Assert.Equal(HttpStatusCode.NoContent, (await Api.DeleteAsync($"api/transport/packages/{first}")).StatusCode);
        var area = JObject.Parse(await Api.GetStringAsync("api/transport/area"))["items"]!;
        Assert.Equal("ALTER TABLE dbo.Orders ADD Channel varchar(10) NULL", (string?)area.Single(item => (string?)item["action"] == "Script")["script"]);
        Assert.Equal(2, area.Count());
        // Open again, as the same version of the same package.
        var reopened = JObject.Parse(await Api.GetStringAsync("api/transport/drafts"))["drafts"]!.Single();
        Assert.Equal(("To be deleted", 1), ((string?)reopened["name"], (int)reopened["version"]!));
        _draft = (string?)reopened["id"];

        // Closed again and received somewhere: from then on it stays.
        var second = (string)(await Json(await Api.PostAsJsonAsync("api/transport/packages", new { draft = _draft, name = "Delivered" }), HttpStatusCode.Created))["package"]!["id"]!;
        server.Services.GetRequiredService<global::ZapMQ.Server.Transport.TransportStore>().Update(second, record =>
            record.Deliveries.Add(new global::ZapMQ.Server.Transport.Delivery { To = "QAS", At = DateTimeOffset.UtcNow, By = "admin" }));

        var refused = await Json(await Api.DeleteAsync($"api/transport/packages/{second}"), HttpStatusCode.Conflict);
        Assert.Contains("entregue a QAS", (string?)refused["error"]);
        Assert.Equal("QAS", (string?)JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{second}"))["package"]!["deliveries"]![0]!["to"]);
        Assert.Empty(JObject.Parse(await Api.GetStringAsync("api/transport/area"))["items"]!);
    }

    [Fact]
    public async Task What_runs_from_a_folder_goes_in_a_package_and_replaces_what_is_at_the_other_side()
    {
        var origin = new PretendDatabase();
        origin.Objects["spNew"] = ("Procedure", "CREATE PROCEDURE dbo.spNew AS RETURN", []);
        origin.Targets["service:Sockets"] = new() { ["server.js"] = "v2", ["lib/io.js"] = "io" };
        byte[] made;
        await using (await Serve(origin))
        {
            await ClearArea();
            Assert.Single(JObject.Parse(await Api.GetStringAsync("api/transport/targets"))["Targets"]!);

            // Brought as a zip made by a careless hand: the folder on the outside, and the configuration in it.
            var zip = PretendDatabase.Zipped(new() { ["Orders.Api.dll"] = "v2", ["wwwroot/index.html"] = "<html>", ["appsettings.json"] = "{dev}", ["web.config"] = "<x/>", ["logs/today.log"] = "noise" }, outer: "publish/");
            var uploaded = await Json(await Api.PostAsync($"api/transport/area/upload?draft={_draft}&kind=api&name=Orders&version=2.3.0", new ByteArrayContent(zip)), HttpStatusCode.Created);
            // The configuration of the zip goes with it, marked; the logs do not.
            Assert.Equal(["appsettings.json", "Orders.Api.dll", "web.config", "wwwroot/index.html"], uploaded["files"]!.Select(file => (string?)file["path"]));
            Assert.Equal(["appsettings.json", "web.config"], uploaded["files"]!.Where(file => (bool)file["environment"]!).Select(file => (string?)file["path"]));
            Assert.Equal(("api", "Orders", "2.3.0", "Replace"), ((string?)uploaded["kind"], (string?)uploaded["name"], (string?)uploaded["version"], (string?)uploaded["action"]));
            // And what is running, packed by the Worker Control.
            var running = await Json(await Api.PostAsJsonAsync("api/transport/area/running", new { draft = _draft, kind = "service", name = "Sockets" }), HttpStatusCode.Created);
            Assert.Equal(("2.0", 2), ((string?)running["version"], running["files"]!.Count()));
            await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { draft = _draft, kind = "Procedure", schema = "dbo", name = "spNew" }), HttpStatusCode.Created);
            await Json(await Api.PostAsync($"api/transport/area/upload?draft={_draft}&kind=database&name=x", new ByteArrayContent(zip)), HttpStatusCode.BadRequest);
            await Json(await Api.PostAsync($"api/transport/area/upload?draft={_draft}&kind=api&name=Orders", new ByteArrayContent(Encoding.UTF8.GetBytes("not a zip"))), HttpStatusCode.BadRequest);
            // A module of a web application, brought as the site is published: its folder is found inside, ...
            var module = await Json(await Api.PostAsync($"api/transport/area/upload?draft={_draft}&kind=frontend&name=brief",
                new ByteArrayContent(PretendDatabase.Zipped(new() { ["mfe/brief/remoteEntry.json"] = "{}", ["mfe/brief/Module-X.js"] = "x", ["mfe/brief/web.config"] = "<x/>" }))), HttpStatusCode.Created);
            // The web.config of the folder of the module is configuration of the environment: it goes, marked.
            Assert.Equal(["Module-X.js", "remoteEntry.json", "web.config"], module["files"]!.Select(file => (string?)file["path"]));
            // ... but the whole site, with the manifest beside the module, would leave the module out of the air in its folder.
            var site = await Json(await Api.PostAsync($"api/transport/area/upload?draft={_draft}&kind=frontend&name=brief",
                new ByteArrayContent(PretendDatabase.Zipped(new() { ["mfe/brief/remoteEntry.json"] = "{}", ["assets/mf.manifest.json"] = "{}" }))), HttpStatusCode.BadRequest);
            Assert.Contains("mfe/brief/", (string?)site["error"]);
            // A module with its files at the root keeps a folder of its own name.
            var rooted = await Json(await Api.PostAsync($"api/transport/area/upload?draft={_draft}&kind=frontend&name=brief",
                new ByteArrayContent(PretendDatabase.Zipped(new() { ["remoteEntry.json"] = "{}", ["brief/logo.png"] = "png", ["assets/a.css"] = "a" }))), HttpStatusCode.Created);
            Assert.Equal(["assets/a.css", "brief/logo.png", "remoteEntry.json"], rooted["files"]!.Select(file => (string?)file["path"]));
            await Api.DeleteAsync($"api/transport/area/{(string?)module["id"]}");
            await Api.DeleteAsync($"api/transport/area/{(string?)rooted["id"]}");

            var closed = await Json(await Api.PostAsJsonAsync("api/transport/packages", new { draft = _draft, name = "Orders 2.3" }), HttpStatusCode.Created);
            // The database first, then what runs.
            Assert.Equal(["database", "api", "service"], closed["items"]!.Select(item => (string?)item["kind"]));
            // The two files of the code, the two of the environment the zip brought.
            Assert.Equal((4, "2.3.0"), ((int)closed["items"]![1]!["files"]!, (string?)closed["items"]![1]!["version"]));
            made = await Released((string?)closed["package"]!["id"]);
        }

        var here = new PretendDatabase();
        here.Targets["api:Orders"] = new() { ["Orders.Api.dll"] = "v1", ["wwwroot/index.html"] = "<html>", ["wwwroot/old.css"] = "x" };
        here.Targets["service:Sockets"] = new() { ["server.js"] = "v2", ["lib/io.js"] = "io" };
        await using var _ = await Serve(here);
        var id = (string)(await Import(FromElsewhere(made)))["package"]!["id"]!;

        var checks = JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{id}/check"))["checks"]!;
        Assert.Equal(["new", "changes", "same"], checks.Select(check => (string?)check["state"]));
        Assert.Equal((0, 1, 1), ((int)checks[1]!["added"]!, (int)checks[1]!["changed"]!, (int)checks[1]!["removed"]!));
        var compared = JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{id}/items/2"));
        Assert.Equal(["Orders.Api.dll:changed", "wwwroot/index.html:same", "wwwroot/old.css:removed"], compared["changes"]!.Select(change => $"{change["path"]}:{change["state"]}"));

        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new { }));
        var applied = await WaitFor(id, "Applied", "Partial", "Failed");

        Assert.Equal("Applied", (string?)applied["package"]!["status"]);
        Assert.Equal(["created", "replaced", "replaced"], applied["results"]!.Select(result => (string?)result["did"] == "altered" ? "created" : (string?)result["did"]));
        Assert.Equal("/backup/there", (string?)applied["results"]![1]!["backup"]);
        lock (here.Applied)
            Assert.Equal(["DatabaseApply", "TransportDeploy", "TransportDeploy"], here.Applied.Select(request => (string?)request["Command"]));
        // What reached the machine is what the package carried, and nothing of the configuration.
        Assert.Equal(new Dictionary<string, string> { ["server.js"] = "v2", ["lib/io.js"] = "io" }, here.Deployed);
        // Nothing is left behind where the files were handed over.
        lock (here.Applied)
            Assert.All(here.Applied.Where(request => (string?)request["Command"] == "TransportDeploy"), request => Assert.False(File.Exists((string)request["File"]!)));
    }

    [Fact]
    public async Task What_is_not_on_this_machine_is_not_approved_and_what_did_not_come_up_stops_the_rest()
    {
        var origin = new PretendDatabase();
        origin.Targets["worker:Orders"] = new() { ["Orders.exe"] = "v2" };
        origin.Targets["frontend:orders"] = new() { ["remoteEntry.js"] = "v2" };
        byte[] made;
        await using (await Serve(origin))
        {
            await ClearArea();
            await Json(await Api.PostAsJsonAsync("api/transport/area/running", new { draft = _draft, kind = "frontend", name = "orders" }), HttpStatusCode.Created);
            await Json(await Api.PostAsJsonAsync("api/transport/area/running", new { draft = _draft, kind = "worker", name = "Orders" }), HttpStatusCode.Created);
            var closed = await Json(await Api.PostAsJsonAsync("api/transport/packages", new { draft = _draft, name = "Orders worker" }), HttpStatusCode.Created);
            // What runs before the screens that call it.
            Assert.Equal(["worker", "frontend"], closed["items"]!.Select(item => (string?)item["kind"]));
            made = await Released((string?)closed["package"]!["id"]);
        }

        // Nowhere to put the worker here: it has to be installed first.
        var bare = new PretendDatabase();
        bare.Targets["frontend:orders"] = new() { ["remoteEntry.js"] = "v1" };
        await using (await Serve(bare))
        {
            var id = (string)(await Import(FromElsewhere(made)))["package"]!["id"]!;
            // The package says what the worker is, so it can be installed here, but not without being told how.
            Assert.Equal(["install", "changes"], JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{id}/check"))["checks"]!.Select(check => (string?)check["state"]));
            var refused = await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new { }), HttpStatusCode.Conflict);
            Assert.Contains("não existe neste ambiente", (string?)refused["error"]);
        }

        var here = new PretendDatabase
        {
            OnApply = request => (string?)request["Kind"] == "worker"
                ? new JObject { ["Ok"] = true, ["Applied"] = false, ["Replaced"] = true, ["Problem"] = "The files were replaced, but it did not come up again", ["Backup"] = "/backup/x", ["Messages"] = new JArray("stopped", "replaced") }
                : null
        };
        here.Targets["worker:Orders"] = new() { ["Orders.exe"] = "v1" };
        here.Targets["frontend:orders"] = new() { ["remoteEntry.js"] = "v1" };
        await using var _ = await Serve(here);
        var failing = (string)(await Import(FromElsewhere(made)))["package"]!["id"]!;
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{failing}/approve", new { }));
        await WaitFor(failing, "Paused", "Applied", "Partial", "Failed");
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{failing}/decide", new { proceed = false }));
        var ended = await WaitFor(failing, "Applied", "Partial", "Failed");

        Assert.Equal("Failed", (string?)ended["package"]!["status"]);
        Assert.Equal(("failed", "/backup/x"), ((string?)ended["results"]!.Single()["status"], (string?)ended["results"]![0]!["backup"]));
        lock (here.Applied)
        {
            Assert.False((bool)here.Applied.Single()["Force"]!);
            here.Applied.Clear();
        }

        // Nothing of it was applied: somebody may ask again, this time for what does not stop to be ended.
        here.OnApply = null;
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{failing}/approve", new { force = true }));
        var again = await WaitFor(failing, "Applied", "Partial", "Failed");
        Assert.Equal(("Applied", true), ((string?)again["package"]!["status"], (bool)again["package"]!["force"]!));
        lock (here.Applied)
            Assert.All(here.Applied, request => Assert.True((bool)request["Force"]!));
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{failing}/revert", new { force = true }));
        await WaitFor(failing, "Reverted");
        lock (here.Reverted)
            Assert.All(here.Reverted, request => Assert.True((bool)request["Force"]!));
    }

    [Fact]
    public async Task An_application_this_environment_does_not_have_is_installed_from_the_package_with_its_secrets_filled_here()
    {
        var origin = new PretendDatabase();
        origin.Targets["api:Orders"] = new() { ["Orders.Api.dll"] = "v2" };
        origin.Sites["Orders"] = ["Orders 1", "Orders 2"];
        origin.Defaults["Sites"] = new JArray(new JObject { ["Name"] = "Orders 1", ["Port"] = 9024 }, new JObject { ["Name"] = "Orders 2", ["Port"] = 9025 }, new JObject { ["Name"] = "Other 1", ["Port"] = 9030 });
        origin.Settings["api:Orders"] = new()
        {
            ["appsettings.json"] = """{ "ConnectionStrings": { "Main": "Server=db;Database=Sales;User Id=app;Password=s3cr3t;" }, "Jwt": { "Secret": "abc", "Issuer": "Orders" }, "Port": 80 }""",
            ["web.config"] = """<configuration><appSettings><add key="ApiPassword" value="xyz" /><add key="Mode" value="dev" /></appSettings></configuration>"""
        };
        origin.ProxyFiles["nginx.conf"] = """
            http {
              upstream orders { server 127.0.0.1:9024; server 127.0.0.1:9025; }
              upstream other { server 127.0.0.1:9030; }
              server {
                location /api/orders/ { proxy_pass http://orders/; }
                location /api/other/ { proxy_pass http://other/; }
              }
            }
            """;
        byte[] made;
        await using (await Serve(origin))
        {
            await ClearArea();
            await Json(await Api.PostAsJsonAsync("api/transport/area/running", new { draft = _draft, kind = "api", name = "Orders" }), HttpStatusCode.Created);
            var closed = await Json(await Api.PostAsJsonAsync("api/transport/packages", new { draft = _draft, name = "Orders new" }), HttpStatusCode.Created);
            var install = closed["items"]![0]!["install"]!;
            Assert.Equal((2, "9024,9025"), ((int)install["instances"]!, string.Join(",", install["ports"]!.Select(port => (int)port))));
            // No secret of here goes along.
            var settings = install["settings"]!.ToDictionary(setting => (string)setting["path"]!, setting => (string)setting["content"]!);
            Assert.DoesNotContain("s3cr3t", settings["appsettings.json"]);
            Assert.DoesNotContain("abc", settings["appsettings.json"]);
            Assert.Contains("Orders", settings["appsettings.json"]);
            Assert.DoesNotContain("xyz", settings["web.config"]);
            Assert.Contains("value=\"dev\"", settings["web.config"]);
            Assert.Equal(["ConnectionStrings:Main (senha)", "Jwt:Secret"], install["settings"]![0]!["blanked"]!.Select(name => (string?)name));
            // The lines of the proxy that are about it, and only those.
            var proxy = (string)install["proxy"]!;
            Assert.Contains("upstream orders", proxy);
            Assert.Contains("location /api/orders/", proxy);
            Assert.DoesNotContain("other", proxy);
            made = await Released((string?)closed["package"]!["id"]);
        }

        var here = new PretendDatabase();
        await using var _ = await Serve(here);
        var id = (string)(await Import(FromElsewhere(made)))["package"]!["id"]!;
        Assert.Equal("install", (string?)JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{id}/check"))["checks"]![0]!["state"]);
        var refused = await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new { }), HttpStatusCode.Conflict);
        Assert.Contains("informe como instalá-lo", (string?)refused["error"]);

        var filled = """{ "ConnectionStrings": { "Main": "Server=qas;Password=other;" } }""";
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new
        {
            installs = new Dictionary<string, object> { ["1"] = new { folder = "E:\\Apps\\Orders", instances = 2, port = 19024, siteName = "{name} {n}", settings = new[] { new { path = "appsettings.json", content = filled } } } }
        }));
        var applied = await WaitFor(id, "Applied", "Partial", "Failed");
        Assert.Equal(("Applied", true), ((string?)applied["package"]!["status"], (bool)applied["results"]![0]!["installed"]!));
        JObject created;
        lock (here.Applied)
            created = here.Applied.Single(request => (string?)request["Command"] == "ApplicationCreate");
        Assert.Equal(("api", "Orders", "E:\\Apps\\Orders", 2, 19024), ((string?)created["Kind"], (string?)created["Name"], (string?)created["Folder"], (int)created["Instances"]!, (int)created["Port"]!));
        Assert.Equal(filled, (string?)created["Configs"]![0]!["Content"]);
        Assert.Equal(new Dictionary<string, string> { ["Orders.Api.dll"] = "v2" }, here.Deployed);

        // What was installed is not taken away by putting the package back.
        var plan = JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{id}/revert"));
        Assert.Equal("cannot", (string?)plan["steps"]![0]!["state"]);
    }

    [Fact]
    public async Task A_service_run_by_nssm_is_installed_with_its_program_and_its_script()
    {
        var origin = new PretendDatabase();
        origin.Targets["service:Sockets"] = new() { ["index.js"] = "server", ["package.json"] = "{}" };
        origin.Defaults["Services"] = new JArray(new JObject
        {
            ["Name"] = "Sockets", ["DisplayName"] = "Ojitech Sockets", ["StartType"] = "Automatic", ["Path"] = "D:\\install\\nssm\\nssm.exe",
            ["Program"] = "C:\\Program Files\\nodejs\\node.exe", ["Script"] = "D:\\Apps\\Sockets\\index.js"
        });
        byte[] made;
        await using (await Serve(origin))
        {
            await ClearArea();
            await Json(await Api.PostAsJsonAsync("api/transport/area/running", new { draft = _draft, kind = "service", name = "Sockets" }), HttpStatusCode.Created);
            var closed = await Json(await Api.PostAsJsonAsync("api/transport/packages", new { draft = _draft, name = "Sockets" }), HttpStatusCode.Created);
            var install = closed["items"]![0]!["install"]!;
            Assert.Equal(("C:\\Program Files\\nodejs\\node.exe", "index.js", "D:\\install\\nssm\\nssm.exe"), ((string?)install["program"], (string?)install["executable"], (string?)install["wrapper"]));
            made = await Released((string?)closed["package"]!["id"]);
        }

        var here = new PretendDatabase();
        await using var _ = await Serve(here);
        var id = (string)(await Import(FromElsewhere(made)))["package"]!["id"]!;
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new
        {
            installs = new Dictionary<string, object> { ["1"] = new { folder = "D:\\OjiTec\\services\\sockets", executable = "index.js", program = "C:\\node\\node.exe", wrapper = "E:\\nssm\\nssm.exe" } }
        }));
        await WaitFor(id, "Applied");
        JObject created;
        lock (here.Applied)
            created = here.Applied.Single(request => (string?)request["Command"] == "ApplicationCreate");
        Assert.Equal(("service", "index.js", "C:\\node\\node.exe", "E:\\nssm\\nssm.exe"), ((string?)created["Kind"], (string?)created["Executable"], (string?)created["Program"], (string?)created["Wrapper"]));
    }

    [Fact]
    public async Task A_web_application_goes_only_to_one_of_the_same_kind()
    {
        var origin = new PretendDatabase();
        origin.Targets["frontend:Portal"] = new() { ["index.html"] = "shell", ["assets/mf.manifest.json"] = "{}", ["mfe/orders/remoteEntry.js"] = "p" };
        origin.Modes["frontend:Portal"] = "mfe";
        byte[] made;
        await using (await Serve(origin))
        {
            await ClearArea();
            await Json(await Api.PostAsJsonAsync("api/transport/area/running", new { draft = _draft, kind = "frontend", name = "Portal" }), HttpStatusCode.Created);
            var closed = await Json(await Api.PostAsJsonAsync("api/transport/packages", new { draft = _draft, name = "Portal MFE" }), HttpStatusCode.Created);
            Assert.Equal("mfe", (string?)closed["items"]![0]!["mode"]);
            made = await Released((string?)closed["package"]!["id"]);
        }

        // Here the application of that name is a monolith: it is another application, and nothing is applied.
        var here = new PretendDatabase();
        here.Targets["frontend:Portal"] = new() { ["index.html"] = "monolith", ["main.js"] = "m" };
        here.Modes["frontend:Portal"] = "monolith";
        await using (await Serve(here))
        {
            var id = (string)(await Import(FromElsewhere(made)))["package"]!["id"]!;
            var check = JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{id}/check"))["checks"]![0]!;
            Assert.Equal("mismatch", (string?)check["state"]);
            Assert.Contains("um monolito", (string?)check["problem"]);
            await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new { }), HttpStatusCode.Conflict);
            lock (here.Applied)
                Assert.Empty(here.Applied);
        }

        // Of the same kind, it is replaced as any other application, and nothing says to change its kind.
        var same = new PretendDatabase();
        same.Targets["frontend:Portal"] = new() { ["index.html"] = "older shell" };
        same.Modes["frontend:Portal"] = "mfe";
        await using var _ = await Serve(same);
        var again = (string)(await Import(FromElsewhere(made)))["package"]!["id"]!;
        Assert.Equal("changes", (string?)JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{again}/check"))["checks"]![0]!["state"]);
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{again}/approve", new { }));
        await WaitFor(again, "Applied", "Partial", "Failed");
        lock (same.Applied)
            Assert.Null(same.Applied.Single(request => (string?)request["Command"] == "TransportDeploy")["Mode"]);
    }

    [Fact]
    public async Task A_new_version_left_in_the_inbox_is_applied_where_the_package_was_made_and_then_goes_on()
    {
        var dev = new PretendDatabase();
        dev.Targets["api:Orders"] = new() { ["Orders.Api.dll"] = "v1", ["old.css"] = "x" };
        dev.Incoming["api:Orders"] = new() { ["Orders.Api.dll"] = "v2", ["new.js"] = "n", ["appsettings.json"] = "{of the developer}" };
        await using var _ = await Serve(dev);
        await ClearArea();

        var placed = await Json(await Api.PostAsJsonAsync("api/transport/area/incoming", new { draft = _draft, kind = "api", name = "Orders" }), HttpStatusCode.Created);
        // What the developer left beside the code goes too, marked as configuration of the environment.
        Assert.Equal(["appsettings.json", "new.js", "Orders.Api.dll"], placed["files"]!.Select(file => (string?)file["path"]));
        Assert.Equal(["appsettings.json"], placed["files"]!.Where(file => (bool)file["environment"]!).Select(file => (string?)file["path"]));
        var id = (string)(await Json(await Api.PostAsJsonAsync("api/transport/packages", new { draft = _draft, name = "Orders 2" }), HttpStatusCode.Created))["package"]!["id"]!;

        // Made here, it is compared with what runs here and applied here like anywhere else.
        var check = JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{id}/check"))["checks"]![0]!;
        Assert.Equal(("changes", 1, 1, 1), ((string?)check["state"], (int)check["added"]!, (int)check["changed"]!, (int)check["removed"]!));
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/reject", new { }), HttpStatusCode.Conflict);
        // For later, and taken back: it is closed again, not waiting as one that arrived would be.
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new { at = DateTimeOffset.UtcNow.AddHours(2) }));
        Assert.Equal("Closed", (string?)(await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/reject", new { })))["package"]!["status"]);

        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new { }));
        var applied = await WaitFor(id, "Applied", "Partial", "Failed");

        Assert.Equal("Applied", (string?)applied["package"]!["status"]);
        // What the developer left beside the code was handed over too: the Worker Control puts it only where there is none, or where it was chosen.
        Assert.Equal(new Dictionary<string, string> { ["Orders.Api.dll"] = "v2", ["new.js"] = "n", ["appsettings.json"] = "{of the developer}" }, dev.Deployed);
        Assert.Equal(["closed", "approved", "cancelled", "approved", "applying", "applied"], applied["history"]!.Select(entry => (string?)entry["what"]));
        // Applied, it stays as the record of what was done, and is still the file that goes on.
        Assert.Equal(HttpStatusCode.Conflict, (await Api.DeleteAsync($"api/transport/packages/{id}")).StatusCode);
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new { }), HttpStatusCode.Conflict);
        Assert.NotEmpty(await Released(id));
        Assert.Equal("same", (string?)JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{id}/check"))["checks"]![0]!["state"]);
    }

    [Fact]
    public async Task What_the_machine_that_made_a_zip_left_in_it_is_not_carried()
    {
        await using var _ = await Serve(new PretendDatabase());
        await ClearArea();

        // Zipped on a Mac: __MACOSX beside the folder, a ._ file beside each file, and the listings of Finder and Explorer.
        var zip = PretendDatabase.Zipped(new()
        {
            ["site/Orders.Api.dll"] = "v2", ["site/wwwroot/index.html"] = "<html>", ["site/wwwroot/._index.html"] = "r", ["site/.DS_Store"] = "d",
            ["site/Thumbs.db"] = "t", ["site/desktop.ini"] = "i", ["__MACOSX/site/._Orders.Api.dll"] = "r", ["__MACOSX/site/wwwroot/._index.html"] = "r"
        });

        // The folder on the outside is still found once __MACOSX is out of the way, and only the files of the application are carried.
        var placed = await Json(await Api.PostAsync($"api/transport/area/upload?draft={_draft}&kind=api&name=Orders", new ByteArrayContent(zip)), HttpStatusCode.Created);
        Assert.Equal(["Orders.Api.dll", "wwwroot/index.html"], placed["files"]!.Select(file => (string?)file["path"]));

        // The same for a new application, which carries everything else it has.
        var seen = await Json(await Api.PostAsync("api/transport/applications/files", new ByteArrayContent(zip)));
        Assert.Equal(["Orders.Api.dll", "wwwroot/index.html"], seen["files"]!.Select(file => (string?)file["path"]));

        // A file that only looks like it keeps its place.
        var kept = await Json(await Api.PostAsync($"api/transport/area/upload?draft={_draft}&kind=api&name=Orders2",
            new ByteArrayContent(PretendDatabase.Zipped(new() { ["Orders.dll"] = "v2", ["MACOSX/readme.txt"] = "x", ["data._bak"] = "b", [".DS_Store.txt"] = "n" }))), HttpStatusCode.Created);
        Assert.Equal([".DS_Store.txt", "data._bak", "MACOSX/readme.txt", "Orders.dll"], kept["files"]!.Select(file => (string?)file["path"]));

        // A zip with nothing but that is refused, as an empty one is.
        await Json(await Api.PostAsync($"api/transport/area/upload?draft={_draft}&kind=api&name=Orders3",
            new ByteArrayContent(PretendDatabase.Zipped(new() { ["__MACOSX/._a"] = "r", [".DS_Store"] = "d" }))), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_configuration_of_the_environment_travels_without_secrets_and_is_replaced_only_where_it_was_chosen()
    {
        byte[] made;
        await using (await Serve(new PretendDatabase()))
        {
            await ClearArea();
            // What an application publishes beside its code: configuration with a secret, configuration without one, and what never travels.
            var zip = PretendDatabase.Zipped(new()
            {
                ["Orders.Api.dll"] = "v2", ["env.json"] = "{\"apiBaseUrl\": \"http://dev\"}", ["appsettings.json"] = "{\"Port\": 1, \"Db\": {\"Password\": \"s3cret\", \"Server\": \"dev\"}}",
                ["web.config"] = "<configuration/>", ["ConfigWorkers.json"] = "{}", ["data.db"] = "db", ["logs/today.log"] = "noise", ["logs/appsettings.json"] = "{}"
            });
            var uploaded = await Json(await Api.PostAsync($"api/transport/area/upload?draft={_draft}&kind=api&name=Orders&version=2.0.0", new ByteArrayContent(zip)), HttpStatusCode.Created);
            var files = uploaded["files"]!.ToDictionary(file => (string)file["path"]!);
            Assert.Equal(new[] { "Orders.Api.dll", "appsettings.json", "env.json", "web.config" }.Order(), files.Keys.Order());
            Assert.Equal([false, true, true, true], new[] { "Orders.Api.dll", "appsettings.json", "env.json", "web.config" }.Select(path => (bool)files[path]["environment"]!));
            Assert.Equal(["Db:Password"], files["appsettings.json"]["blanked"]!.Select(name => (string?)name));
            Assert.Empty(files["env.json"]["blanked"]!);

            var closed = await Json(await Api.PostAsJsonAsync("api/transport/packages", new { draft = _draft, name = "Orders 2.0" }), HttpStatusCode.Created);
            made = await Released((string?)closed["package"]!["id"]);
            // The secret is not in the package, and what has none is exactly as it was.
            using var package = new ZipArchive(new MemoryStream(made));
            var carried = PretendDatabase.Unzipped(ZipOf(package));
            Assert.DoesNotContain("s3cret", carried["appsettings.json"]);
            Assert.Contains("\"Server\": \"dev\"", carried["appsettings.json"]);
            Assert.Equal("{\"apiBaseUrl\": \"http://dev\"}", carried["env.json"]);
        }

        var here = new PretendDatabase();
        here.Targets["api:Orders"] = new() { ["Orders.Api.dll"] = "v1" };
        // Here: env.json differs; appsettings.json is the same but for the secret (which this environment keeps); web.config is missing.
        here.EnvironmentHere["api:Orders"] = new() { ["env.json"] = "{\"apiBaseUrl\": \"http://qas\"}", ["appsettings.json"] = "{\"Port\": 1, \"Db\": {\"Password\": \"mine\", \"Server\": \"dev\"}}" };
        await using var _ = await Serve(here);
        var id = (string)(await Import(FromElsewhere(made)))["package"]!["id"]!;

        var check = JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{id}/check"))["checks"]![0]!;
        Assert.Equal(("changes", 0, 1, 0), ((string?)check["state"], (int)check["added"]!, (int)check["changed"]!, (int)check["removed"]!));
        Assert.Equal(["appsettings.json:same", "env.json:different", "web.config:new"], check["environment"]!.Select(file => $"{file["path"]}:{file["state"]}"));

        // The one that is the same, or that does not exist here, is not for choosing; and nothing is applied on a wrong word.
        var wrong = await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new { environment = new Dictionary<int, string[]> { [1] = ["appsettings.json"] } }), HttpStatusCode.Conflict);
        Assert.Contains("appsettings.json", (string?)wrong["error"]);
        Assert.Equal("Pending", (string?)JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{id}"))["package"]!["status"]);

        here.OnApply = _ => new JObject { ["Ok"] = true, ["Applied"] = true, ["Did"] = "replaced", ["Backup"] = "/backup/there", ["EnvironmentCreated"] = new JArray("web.config"), ["EnvironmentReplaced"] = new JArray("env.json"), ["Messages"] = new JArray("replaced") };
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new { environment = new Dictionary<int, string[]> { [1] = ["env.json"] } }));
        var ended = await WaitFor(id, "Applied", "Partial", "Failed");

        Assert.Equal("Applied", (string?)ended["package"]!["status"]);
        lock (here.Applied)
            Assert.Equal(["env.json"], here.Applied.Single()["ReplaceEnvironment"]!.Select(path => (string?)path));
        var messages = ended["results"]![0]!["messages"]!.Select(message => (string?)message).ToList();
        Assert.Contains(messages, message => message!.StartsWith("web.config não existia aqui e foi criado") && message.Contains("pode variar de um ambiente para o outro"));
        Assert.Contains(messages, message => message!.StartsWith("env.json foi substituído pelo do pacote"));
    }

    [Fact]
    public async Task A_worker_control_that_does_not_tell_what_configuration_it_has_is_not_promised_to_put_it()
    {
        byte[] made;
        await using (await Serve(new PretendDatabase()))
        {
            await ClearArea();
            var zip = PretendDatabase.Zipped(new() { ["Orders.Api.dll"] = "v2", ["env.json"] = "{}" });
            await Json(await Api.PostAsync($"api/transport/area/upload?draft={_draft}&kind=api&name=Orders", new ByteArrayContent(zip)), HttpStatusCode.Created);
            made = await Released((string?)(await Json(await Api.PostAsJsonAsync("api/transport/packages", new { draft = _draft, name = "Orders" }), HttpStatusCode.Created))["package"]!["id"]);
        }

        var here = new PretendDatabase { OldWorker = true };
        here.Targets["api:Orders"] = new() { ["Orders.Api.dll"] = "v1" };
        await using var _ = await Serve(here);
        var id = (string)(await Import(FromElsewhere(made)))["package"]!["id"]!;

        var check = JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{id}/check"))["checks"]![0]!;
        Assert.Equal(["env.json:unknown"], check["environment"]!.Select(file => $"{file["path"]}:{file["state"]}"));
        // And nothing can be chosen about it.
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new { environment = new Dictionary<int, string[]> { [1] = ["env.json"] } }), HttpStatusCode.Conflict);
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var memory = new MemoryStream();
        using (var stream = entry.Open())
            stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>
    /// The files of the first item of a package, as the package carries them.
    /// </summary>
    private static byte[] ZipOf(ZipArchive package) => ReadEntry(package.Entries.First(entry => entry.FullName.EndsWith("files.zip")));

    [Fact]
    public async Task A_file_to_download_is_brought_as_it_is_and_goes_with_the_version_it_was_given()
    {
        var dev = new PretendDatabase();
        dev.Targets["download:app-shop.apk"] = new() { ["app-shop.apk"] = "shop v1" };
        await using var _ = await Serve(dev);
        await ClearArea();

        // An app is a zip itself: it is taken as the file it is, not opened.
        var apk = PretendDatabase.Zipped(new() { ["classes.dex"] = "dex", ["AndroidManifest.xml"] = "<manifest/>" });
        var placed = await Json(await Api.PostAsync($"api/transport/area/upload?draft={_draft}&kind=download&name=app-shop.apk&version=2.1.0", new ByteArrayContent(apk)), HttpStatusCode.Created);
        Assert.Equal(("app-shop.apk", "2.1.0"), ((string?)placed["files"]!.Single()["path"], (string?)placed["version"]));
        await Json(await Api.PostAsync($"api/transport/area/upload?draft={_draft}&kind=download&name=..%5Coutside.apk", new ByteArrayContent(apk)), HttpStatusCode.BadRequest);

        var id = (string)(await Json(await Api.PostAsJsonAsync("api/transport/packages", new { draft = _draft, name = "App 2.1.0" }), HttpStatusCode.Created))["package"]!["id"]!;
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new { }));
        await WaitFor(id, "Applied");

        lock (dev.Applied)
            Assert.Equal(("download", "app-shop.apk", "2.1.0"), ((string?)dev.Applied.Last()["Kind"], (string?)dev.Applied.Last()["Name"], (string?)dev.Applied.Last()["Version"]));
        // The file went alone, under its own name.
        Assert.Equal(["app-shop.apk"], dev.Deployed!.Keys);
    }

    [Fact]
    public async Task A_new_application_is_brought_as_a_zip_looked_at_and_created_by_the_worker_control()
    {
        var machine = new PretendDatabase();
        await using var _ = await Serve(machine);
        Assert.True((bool)JObject.Parse(await Api.GetStringAsync("api/transport/applications/defaults"))["Allowed"]!);

        // Zipped with the folder on the outside, and with its configuration: all of it goes.
        var zip = PretendDatabase.Zipped(new() { ["Orders.exe"] = "v1", ["tools/Fix.exe"] = "f", ["appsettings.json"] = "{published}", ["Orders.ini"] = "[a]", ["lib/Core.dll"] = "c" }, "publish/");
        var seen = await Json(await Api.PostAsync("api/transport/applications/files", new ByteArrayContent(zip)));
        Assert.Equal(["Orders.exe", "tools/Fix.exe"], seen["executables"]!.Select(path => (string?)path));
        Assert.Equal([("appsettings.json", "{published}"), ("Orders.ini", "[a]")], seen["settings"]!.Select(setting => ((string?)setting["path"], (string?)setting["content"])));
        Assert.Equal(5, seen["files"]!.Count());
        var token = (string?)seen["token"];

        machine.OnApply = _ => new JObject { ["Ok"] = true, ["Created"] = false, ["Problem"] = "The folder already has something in it", ["Messages"] = new JArray() };
        var wanted = new { token, kind = "service", name = "Orders", folder = "D:\\services\\orders", executable = "Orders.exe", instances = 1, port = 0, displayName = "Orders", startType = "Automatic", settings = new[] { new { path = "appsettings.json", content = "{of this place}" } } };
        Assert.False((bool)(await Json(await Api.PostAsJsonAsync("api/transport/applications", wanted)))["Created"]!);
        // Not created, the files are still there to try again.
        machine.OnApply = null;
        var created = await Json(await Api.PostAsJsonAsync("api/transport/applications", wanted));
        Assert.True((bool)created["Created"]!);
        lock (machine.Applied)
        {
            var asked = machine.Applied.Last();
            Assert.Equal(("service", "Orders", "Orders.exe", "admin", "{of this place}"), ((string?)asked["Kind"], (string?)asked["Name"], (string?)asked["Executable"], (string?)asked["By"], (string?)asked["Configs"]![0]!["Content"]));
        }
        Assert.Equal("{published}", machine.Deployed!["appsettings.json"]);
        // Created, the zip is gone: the same upload does not make another.
        await Json(await Api.PostAsJsonAsync("api/transport/applications", wanted), HttpStatusCode.Conflict);
        await Json(await Api.PostAsJsonAsync("api/transport/applications", new { token = "../../etc", kind = "service", name = "x" }), HttpStatusCode.Conflict);

        var removed = await Json(await Api.DeleteAsync("api/transport/applications/service/Orders?force=true"));
        Assert.True((bool)removed["Removed"]!);
        lock (machine.Applied)
            Assert.Equal(("ApplicationRemove", "service", "Orders", true), ((string?)machine.Applied.Last()["Command"], (string?)machine.Applied.Last()["Kind"], (string?)machine.Applied.Last()["Name"], (bool)machine.Applied.Last()["Force"]!));
    }

    [Fact]
    public async Task The_files_of_the_environment_and_the_inboxes_are_asked_of_the_worker_control()
    {
        var asked = new List<JObject>();
        var worker = await V2Client.ConnectAsync(server.Port, "WorkerControl");
        await using var _ = worker;
        await worker.RequestAsync("bind", "WorkerControlAdmin");
        var listening = Task.Run(async () =>
        {
            while (true)
            {
                var delivery = await worker.NextPushAsync();
                var request = (JObject)delivery["message"]!["body"]!;
                lock (asked)
                    asked.Add(request);
                var answer = (string?)request["Path"] == "App.exe"
                    ? new JObject { ["Ok"] = false, ["Error"] = new JObject { ["Code"] = "not-found", ["Message"] = "not a file of the environment" } }
                    : (string?)request["Sha256"] == "old"
                        ? new JObject { ["Ok"] = false, ["Error"] = new JObject { ["Code"] = "invalid-state", ["Message"] = "changed by somebody else" } }
                        : new JObject { ["Ok"] = true, ["Content"] = "{ }", ["Sha256"] = "abc" };
                await worker.RequestAsync("respond", "WorkerControlAdmin", frame => { frame["messageId"] = delivery["message"]!["id"]; frame["response"] = answer; });
            }
        });

        Assert.Equal("{ }", (string?)JObject.Parse(await Api.GetStringAsync("api/transport/file?kind=api&name=Orders&instance=1&path=appsettings.json"))["Content"]);
        Assert.Equal(HttpStatusCode.OK, (await Api.PutAsJsonAsync("api/transport/file", new { kind = "api", name = "Orders", instance = 1, path = "appsettings.json", content = "{ \"a\": 1 }", sha256 = "abc", restart = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Api.PutAsJsonAsync("api/transport/file", new { kind = "api", name = "Orders", path = "appsettings.json", content = "{}", sha256 = "old" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.GetAsync("api/transport/file?kind=api&name=Orders&path=App.exe")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync("api/transport/files?kind=api&name=Orders")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync("api/transport/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Api.PutAsJsonAsync("api/transport/settings/inboxes", new Dictionary<string, string?> { ["api"] = "D:\\drop\\api", ["worker"] = "" })).StatusCode);

        lock (asked)
        {
            Assert.Equal(["TransportFile", "SetTransportFile", "SetTransportFile", "TransportFile", "TransportFiles", "TransportSettings", "SetTransportInboxes"], asked.Select(request => (string?)request["Command"]));
            Assert.Equal((1, "appsettings.json", "admin"), ((int)asked[0]["Instance"]!, (string?)asked[0]["Path"], (string?)asked[0]["By"]));
            Assert.Equal(("{ \"a\": 1 }", "abc", true), ((string?)asked[1]["Content"], (string?)asked[1]["Sha256"], (bool)asked[1]["Restart"]!));
            Assert.Equal(("D:\\drop\\api", ""), ((string?)asked[6]["Inboxes"]!["api"], (string?)asked[6]["Inboxes"]!["worker"]));
        }
        using var anonymous = new HttpClient { BaseAddress = new Uri($"http://localhost:{server.PanelPort}/") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("api/transport/file?kind=api&name=Orders&path=appsettings.json")).StatusCode);
    }
}
