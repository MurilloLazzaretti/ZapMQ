using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using ZapMQ.Server.Transport;

namespace ZapMQ.Server.Tests;

/// <summary>
/// A package sent from one environment to another over HTTP: two servers in this process, DEV
/// and QAS, a route from the first to the second, and the door of the second, which takes only
/// what an authorized origin signed.
/// </summary>
public sealed class TransportRouteTests : IAsyncLifetime
{
    private readonly ServerFixture _dev = new() { Environment = "DEV" };
    private readonly ServerFixture _qas = new() { Environment = "QAS" };

    public async Task InitializeAsync()
    {
        await _dev.InitializeAsync();
        await _qas.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _dev.DisposeAsync();
        await _qas.DisposeAsync();
    }

    private static async Task<JObject> Json(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? [] : JObject.Parse(text);
    }

    /// <summary>
    /// A package with one script of the given size, as if made in a third environment, imported in
    /// DEV and applied there: what may go on to the next one.
    /// </summary>
    private async Task<(string Id, byte[] Bytes)> AppliedInDev(int scriptBytes)
    {
        var id = Guid.NewGuid().ToString("N");
        var script = "CREATE PROCEDURE dbo.Big AS SELECT 1\n--" + new string('x', scriptBytes);
        var content = Encoding.UTF8.GetBytes(script);
        var manifest = new JObject
        {
            ["format"] = 1, ["id"] = id, ["name"] = "Grande", ["origin"] = "LAB", ["createdAt"] = DateTimeOffset.UtcNow, ["createdBy"] = "ana",
            ["items"] = new JArray(new JObject
            {
                ["number"] = 1, ["kind"] = "database", ["action"] = "Define", ["objectKind"] = "Procedure", ["schema"] = "dbo", ["name"] = "Big",
                ["file"] = "items/1.sql", ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(content))
            })
        };
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var entry = zip.CreateEntry("package.json").Open())
                entry.Write(Encoding.UTF8.GetBytes(manifest.ToString()));
            using (var entry = zip.CreateEntry("items/1.sql", CompressionLevel.NoCompression).Open())
                entry.Write(content);
        }
        var bytes = memory.ToArray();
        await Json(await _dev.Admin.PostAsync("api/transport/packages/import", new ByteArrayContent(bytes)), HttpStatusCode.Created);
        _dev.Services.GetRequiredService<TransportStore>().Update(id, record => record.Status = "Applied");
        return (id, bytes);
    }

    /// <summary>
    /// QAS lets DEV in, and DEV gets a route to QAS with the key QAS gave.
    /// </summary>
    private async Task<(string Route, string Key)> Linked()
    {
        var origin = await Json(await _qas.Admin.PostAsJsonAsync("api/transport/origins", new { name = "DEV" }), HttpStatusCode.Created);
        var key = (string)origin["key"]!;
        Assert.StartsWith("zt1.", key);
        var route = await Json(await _dev.Admin.PostAsJsonAsync("api/transport/routes",
            new { name = "QAS", url = $"http://localhost:{_qas.PanelPort}/", key, allowHttp = true }), HttpStatusCode.Created);
        return ((string)route["id"]!, key);
    }

    private async Task<JObject> Sent(string id)
    {
        for (var attempt = 0; attempt < 600; attempt++)
        {
            var sends = JArray.Parse(await _dev.Admin.GetStringAsync($"api/transport/packages/{id}/sends"));
            if (sends.Count > 0 && (string?)sends[0]["status"] is "delivered" or "failed")
                return (JObject)sends[0];
            await Task.Delay(50);
        }
        throw new TimeoutException("the package was not sent: " + await _dev.Admin.GetStringAsync($"api/transport/packages/{id}/sends"));
    }

    [Fact]
    public async Task A_package_goes_along_a_route_in_parts_and_arrives_pending_on_the_other_side()
    {
        var (id, bytes) = await AppliedInDev(9 * 1024 * 1024);
        var (route, _) = await Linked();

        // The test proves the address, the key and the clocks.
        Assert.Equal("QAS", (string?)(await Json(await _dev.Admin.PostAsync($"api/transport/routes/{route}/test", null), HttpStatusCode.OK))["environment"]);

        await Json(await _dev.Admin.PostAsJsonAsync($"api/transport/packages/{id}/send", new { route }), HttpStatusCode.Accepted);
        var sent = await Sent(id);
        Assert.True((string?)sent["status"] == "delivered", (string?)sent["problem"]);
        Assert.Equal(bytes.Length, (long)sent["sent"]!);
        Assert.Null((string?)sent["note"]);

        // On the other side it waits for someone of QAS to approve it.
        var there = JObject.Parse(await _qas.Admin.GetStringAsync($"api/transport/packages/{id}"));
        Assert.Equal(("Pending", true), ((string?)there["package"]!["status"], (bool)there["package"]!["received"]!));
        Assert.Equal("DEV (envio)", (string?)there["history"]![0]!["by"]);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), (string?)there["sha256"]);

        // Here it is marked as delivered, and cannot be deleted any more.
        var here = JObject.Parse(await _dev.Admin.GetStringAsync($"api/transport/packages/{id}"));
        Assert.Equal("QAS", (string?)here["package"]!["deliveries"]![0]!["to"]);
        Assert.Contains(here["history"]!, entry => (string?)entry["what"] == "sent");

        // Sending again finds it there and is done at once, saying so.
        await Json(await _dev.Admin.PostAsJsonAsync($"api/transport/packages/{id}/send", new { route }), HttpStatusCode.Accepted);
        var again = await Sent(id);
        Assert.Equal("delivered", (string?)again["status"]);
        Assert.Contains("QAS já tinha este pacote", (string?)again["note"]);
    }

    [Fact]
    public async Task Only_an_origin_with_the_key_gets_a_package_in()
    {
        var (id, _) = await AppliedInDev(100);

        // QAS was never told about DEV.
        var route = await Json(await _dev.Admin.PostAsJsonAsync("api/transport/routes",
            new { name = "QAS", url = $"http://localhost:{_qas.PanelPort}", key = TransportSecret.Show(RandomNumberGenerator.GetBytes(32)), allowHttp = true }), HttpStatusCode.Created);
        var test = await Json(await _dev.Admin.PostAsync($"api/transport/routes/{route["id"]}/test", null), HttpStatusCode.BadGateway);
        Assert.Contains("não é uma origem autorizada", (string?)test["error"]);

        // Told, but the key DEV has is not the one QAS gave.
        await Json(await _qas.Admin.PostAsJsonAsync("api/transport/origins", new { name = "DEV" }), HttpStatusCode.Created);
        test = await Json(await _dev.Admin.PostAsync($"api/transport/routes/{route["id"]}/test", null), HttpStatusCode.BadGateway);
        Assert.Contains("assinatura inválida", (string?)test["error"]);
        await Json(await _dev.Admin.PostAsJsonAsync($"api/transport/packages/{id}/send", new { route = (string)route["id"]! }), HttpStatusCode.Accepted);
        Assert.Equal("failed", (string?)(await Sent(id))["status"]);
        await Json(await _qas.Admin.GetAsync($"api/transport/packages/{id}"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_door_refuses_what_is_unsigned_old_heard_before_or_changed()
    {
        var (_, key) = await Linked();
        var bytes = TransportSecret.Read(key)!;
        using var raw = new HttpClient { BaseAddress = new Uri($"http://localhost:{_qas.PanelPort}/") };

        HttpRequestMessage Hello(DateTimeOffset at, byte[]? signedBody = null, byte[]? sentBody = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "api/transport/inbound/hello") { Content = new ByteArrayContent(sentBody ?? signedBody ?? "{}"u8.ToArray()) };
            TransportSignature.Apply(request, "DEV", bytes, "/hello", signedBody ?? "{}"u8.ToArray(), at);
            return request;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await raw.PostAsync("api/transport/inbound/hello", new StringContent("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await raw.SendAsync(Hello(DateTimeOffset.UtcNow.AddMinutes(-10)))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await raw.SendAsync(Hello(DateTimeOffset.UtcNow, "{}"u8.ToArray(), "{\"x\":1}"u8.ToArray()))).StatusCode);

        // The same signed request, heard and sent a second time.
        var once = Hello(DateTimeOffset.UtcNow);
        var copy = new HttpRequestMessage(HttpMethod.Post, once.RequestUri) { Content = new ByteArrayContent("{}"u8.ToArray()) };
        foreach (var header in once.Headers)
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        Assert.Equal(HttpStatusCode.OK, (await raw.SendAsync(once)).StatusCode);
        var replayed = await Json(await raw.SendAsync(copy), HttpStatusCode.Unauthorized);
        Assert.Contains("já foi recebido", (string?)replayed["error"]);

        // The rest of the panel stays behind its login.
        Assert.Equal(HttpStatusCode.Unauthorized, (await raw.GetAsync("api/transport/routes")).StatusCode);
    }

    [Fact]
    public async Task A_route_is_https_unless_asked_and_its_key_is_never_shown()
    {
        var key = TransportSecret.Show(RandomNumberGenerator.GetBytes(32));
        var plain = await Json(await _dev.Admin.PostAsJsonAsync("api/transport/routes", new { name = "QAS", url = "http://qas.example/zapmq", key }), HttpStatusCode.BadRequest);
        Assert.Contains("https", (string?)plain["error"]);
        await Json(await _dev.Admin.PostAsJsonAsync("api/transport/routes", new { name = "QAS", url = "https://qas.example/zapmq", key = "123" }), HttpStatusCode.BadRequest);
        await Json(await _dev.Admin.PostAsJsonAsync("api/transport/routes", new { name = "QAS", url = "https://qas.example/zapmq/", key }), HttpStatusCode.Created);

        var listed = await _dev.Admin.GetStringAsync("api/transport/routes");
        Assert.DoesNotContain(key[4..], listed);
        Assert.Equal("https://qas.example/zapmq", (string?)JObject.Parse(listed)["routes"]![0]!["url"]);
        // Nor on disk as it was typed, where the machine protects it (elsewhere it is marked plain).
        var disk = File.ReadAllText(Path.Combine(_dev.Services.GetRequiredService<TransportStore>().Root, "routes.json"));
        Assert.Contains(OperatingSystem.IsWindows() ? "dpapi:" : "plain:", disk);
        if (OperatingSystem.IsWindows())
            Assert.DoesNotContain(key[4..], disk);
    }
}
