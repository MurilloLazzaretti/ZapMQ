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
        public string[] Databases { get; set; } = ["Sales"];

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
                case "DatabaseObjects":
                    return new JObject { ["Ok"] = true, ["Objects"] = new JArray(Objects.Select(pair => new JObject { ["Kind"] = pair.Value.Kind, ["Schema"] = "dbo", ["Name"] = pair.Key })) };
                case "DatabaseChanges":
                    return new JObject { ["Ok"] = true, ["Changes"] = new JArray(Changes.Where(change => (string?)change["Name"] == (string?)request["Name"])) };
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

    private async Task<JObject> Json(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return JObject.Parse(text);
    }

    private async Task ClearArea()
    {
        foreach (var item in JObject.Parse(await Api.GetStringAsync("api/transport/area"))["items"]!)
            await Api.DeleteAsync("api/transport/area/" + (string?)item["id"]);
    }

    /// <summary>
    /// The same package as another environment would have made it: under another identity and origin.
    /// </summary>
    private static byte[] FromElsewhere(byte[] package, Func<string, string>? tamper = null)
    {
        using var source = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        using var memory = new MemoryStream();
        using (var target = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in source.Entries)
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                var text = reader.ReadToEnd();
                if (entry.FullName == "package.json")
                {
                    var manifest = JObject.Parse(text);
                    manifest["id"] = Guid.NewGuid().ToString("N");
                    manifest["origin"] = "DEV";
                    text = manifest.ToString();
                }
                else if (tamper is not null)
                    text = tamper(text);
                using var writer = new StreamWriter(target.CreateEntry(entry.FullName).Open(), new UTF8Encoding(false));
                writer.Write(text);
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
            Assert.Equal(JTokenType.Null, (await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { database = "Sales", kind, schema = "dbo", name }), HttpStatusCode.Created))["database"]!.Type);
        // Twice is once; what is not there cannot be carried; what is there cannot go as a drop.
        await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { kind = "View", schema = "dbo", name = "VWORDERS" }), HttpStatusCode.Created);
        await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { kind = "View", schema = "dbo", name = "vwGone" }), HttpStatusCode.NotFound);
        await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { kind = "View", schema = "dbo", name = "vwOrders", drop = true }), HttpStatusCode.BadRequest);
        await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { kind = "Procedure", schema = "dbo", name = "spOld", drop = true }), HttpStatusCode.Created);
        await Json(await Api.PostAsJsonAsync("api/transport/area/scripts", new { title = "Nova coluna", script = "ALTER TABLE dbo.Orders ADD Channel varchar(10) NULL" }), HttpStatusCode.Created);
        await Json(await Api.PostAsJsonAsync("api/transport/area/scripts", new { title = "", script = "x" }), HttpStatusCode.BadRequest);
        Assert.Equal(6, JObject.Parse(await Api.GetStringAsync("api/transport/area"))["items"]!.Count());

        // The script is the one of the moment the package is closed.
        database.Objects["fncTotal"] = ("Function", "CREATE FUNCTION dbo.fncTotal() RETURNS int AS BEGIN RETURN 2 END", []);
        await Json(await Api.PostAsJsonAsync("api/transport/packages", new { name = "" }), HttpStatusCode.BadRequest);
        var closed = await Json(await Api.PostAsJsonAsync("api/transport/packages", new { name = "Fechamento de pedidos", description = "Nova regra" }), HttpStatusCode.Created);

        Assert.Equal(("Closed", false, "TEST", "admin"), ((string?)closed["package"]!["status"], (bool)closed["package"]!["received"]!, (string?)closed["package"]!["origin"], (string?)closed["package"]!["createdBy"]));
        Assert.Equal(["OrderList", null, "fncTotal", "vwOrders", "spClose", "spOld"], closed["items"]!.Select(item => (string?)item["name"]));
        Assert.Equal(["Define", "Script", "Define", "Define", "Define", "Drop"], closed["items"]!.Select(item => (string?)item["action"]));
        Assert.Equal("fp-before-all", (string?)closed["items"]![4]!["base"]);
        Assert.Empty(JObject.Parse(await Api.GetStringAsync("api/transport/area"))["items"]!);
        var id = (string)closed["package"]!["id"]!;
        var third = JObject.Parse(await Api.GetStringAsync($"api/transport/packages/{id}/items/3"));
        Assert.Contains("RETURN 2", (string?)third["script"]);

        // The file is a package that says what it carries and is whole.
        var file = await Api.GetByteArrayAsync($"api/transport/packages/{id}/download");
        using (var zip = new ZipArchive(new MemoryStream(file)))
        {
            Assert.Equal(7, zip.Entries.Count);
            Assert.NotNull(zip.GetEntry("items/02/script.sql"));
        }
        // Where it was made it is neither approved nor taken in again.
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new { }), HttpStatusCode.Conflict);
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
            await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { kind, schema = "dbo", name }), HttpStatusCode.Created);
        await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { kind = "View", schema = "dbo", name = "vwOld", drop = true }), HttpStatusCode.Created);
        var closed = await Json(await Api.PostAsJsonAsync("api/transport/packages", new { name = "Pacote " + Guid.NewGuid().ToString("N")[..6] }), HttpStatusCode.Created);
        return (origin, await Api.GetByteArrayAsync($"api/transport/packages/{(string?)closed["package"]!["id"]}/download"));
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
    public async Task Applying_stops_at_the_first_item_that_fails_and_nothing_is_tried_again()
    {
        var (_, made) = await Made();
        var here = new PretendDatabase
        {
            OnApply = request => (string?)request["Name"] == "spClose"
                ? new JObject { ["Ok"] = true, ["Applied"] = false, ["Problem"] = "Invalid object name 'dbo.Rates'.", ["Batch"] = 1, ["Line"] = 3, ["Messages"] = new JArray() }
                : null
        };
        await using var _ = await Serve(here);
        var id = (string)(await Import(FromElsewhere(made)))["package"]!["id"]!;

        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{id}/approve", new { }));
        var ended = await WaitFor(id, "Applied", "Partial", "Failed");

        Assert.Equal("Partial", (string?)ended["package"]!["status"]);
        Assert.Equal(["applied", "failed"], ended["results"]!.Select(result => (string?)result["status"]));
        Assert.Equal(("Invalid object name 'dbo.Rates'.", 3), ((string?)ended["results"]![1]!["problem"], (int)ended["results"]![1]!["line"]!));
        await Task.Delay(500);
        lock (here.Applied)
            Assert.Equal(2, here.Applied.Count);
        Assert.Equal(1, (int)JObject.Parse(await Api.GetStringAsync("api/transport/summary"))["troubled"]! > 0 ? 1 : 0);
    }

    [Fact]
    public async Task An_approval_may_be_for_later_and_may_be_taken_back_and_a_package_may_be_refused()
    {
        var (_, made) = await Made();
        var here = new PretendDatabase();
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
            Assert.Equal("Sales", (string?)(await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { database = "Sales", kind = "Procedure", schema = "dbo", name = "spNew" }), HttpStatusCode.Created))["database"]);
            var closed = await Json(await Api.PostAsJsonAsync("api/transport/packages", new { name = "Two databases" }), HttpStatusCode.Created);
            made = await Api.GetByteArrayAsync($"api/transport/packages/{(string?)closed["package"]!["id"]}/download");
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
            await Json(await Api.PostAsJsonAsync("api/transport/area/objects", new { kind = "Table", schema = "dbo", name = "Orders" }), HttpStatusCode.Created);
            var closed = await Json(await Api.PostAsJsonAsync("api/transport/packages", new { name = "A table" }), HttpStatusCode.Created);
            made = await Api.GetByteArrayAsync($"api/transport/packages/{(string?)closed["package"]!["id"]}/download");
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

        // Made here: it goes away, file and all, and is not refused.
        await Json(await Api.PostAsJsonAsync($"api/transport/packages/{mine}/reject", new { }), HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.NoContent, (await Api.DeleteAsync($"api/transport/packages/{mine}")).StatusCode);
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
}
