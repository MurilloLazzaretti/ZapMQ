using System.Net;
using System.Net.Http.Json;
using Newtonsoft.Json.Linq;
using ZapMQ.Server.Panel;

namespace ZapMQ.Server.Tests;

/// <summary>
/// The key the panel and the Worker Control seal with what goes over the administration queue.
/// </summary>
public sealed class AdminKeyTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    /// <summary>
    /// A Worker Control as far as the key goes: it takes one while it has none, and from then
    /// on only answers what is sealed with it.
    /// </summary>
    private sealed class PretendWorkerControl
    {
        public byte[]? Key { get; set; }
        public List<JObject> Received { get; } = [];
        public List<string> Asked { get; } = [];

        /// <summary>
        /// Takes the next key and answers something that cannot be read, as when the answer is lost on the way.
        /// </summary>
        public bool LostOnce { get; set; }

        public JObject? Answer(JObject received)
        {
            lock (Received)
                Received.Add(received);
            if ((string?)received["Sealed"] is { } sealedText)
            {
                if (Key is null)
                    return Refusal("no-key");
                if (AdminSeal.Open(Key, sealedText) is not { } plain)
                    return Refusal("unauthenticated");
                var envelope = JObject.Parse(plain);
                var key = Key;
                var answer = Run((JObject)envelope["Request"]!);
                return answer is null ? new JObject { ["Sealed"] = "bm90IHRoZSBhbnN3ZXI=" } : new JObject { ["Sealed"] = AdminSeal.Seal(key, new JObject { ["Id"] = envelope["Id"], ["Answer"] = answer }.ToString(Newtonsoft.Json.Formatting.None)) };
            }
            return Key is null ? Run(received) : Refusal("unauthenticated");
        }

        private static JObject Refusal(string code) => new() { ["Ok"] = false, ["Error"] = new JObject { ["Code"] = code, ["Message"] = "refused" } };

        private JObject? Run(JObject request)
        {
            lock (Asked)
                Asked.Add((string)request["Command"]!);
            if ((string?)request["Command"] != "SetAdminKey")
                return new JObject { ["Ok"] = true, ["Protected"] = Key is not null, ["Version"] = "9.9", ["By"] = request["By"] };
            Key = Convert.FromBase64String((string)request["Key"]!);
            if (!LostOnce)
                return new JObject { ["Ok"] = true, ["Protected"] = true };
            LostOnce = false;
            return null;
        }
    }

    private async Task<V2Client> Serve(PretendWorkerControl control)
    {
        var client = await V2Client.ConnectAsync(server.Port, "WorkerControl");
        await client.RequestAsync("bind", "WorkerControlAdmin");
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var delivery = await client.NextPushAsync();
                if (control.Answer((JObject)delivery["message"]!["body"]!) is { } answer)
                    await client.RequestAsync("respond", "WorkerControlAdmin", frame =>
                    {
                        frame["messageId"] = delivery["message"]!["id"];
                        frame["response"] = answer;
                    });
            }
        });
        return client;
    }

    [Fact]
    public void What_is_sealed_is_the_same_at_both_ends()
    {
        byte[] key = [.. Enumerable.Range(1, 32).Select(value => (byte)value)];
        // The Worker Control seals the same text with the same key and nonce to the same bytes: the two ends are written apart.
        var sealedText = AdminSeal.Seal(key, "{\"Command\":\"Status\"}", [.. Enumerable.Range(0, 12).Select(value => (byte)value)]);

        Assert.Equal("AAECAwQFBgcICQoLBBEtFsASKUHHOzd0vw2BuwoSQvilsABlawmIYzfslvlc5J/o", sealedText);
        Assert.Equal("{\"Command\":\"Status\"}", AdminSeal.Open(key, sealedText));
        Assert.Null(AdminSeal.Open(new byte[32], sealedText));
    }

    [Fact]
    public async Task The_master_protects_the_queue_and_from_then_on_everything_asked_is_sealed()
    {
        var control = new PretendWorkerControl();
        await using var _ = await Serve(control);

        // Before there is a key, it is asked as it always was.
        Assert.False((bool)JObject.Parse(await server.Admin.GetStringAsync("api/workers/status"))["Protected"]!);
        Assert.False((bool)JObject.Parse(await server.Admin.GetStringAsync("api/workers/protection"))["hasKey"]!);
        lock (control.Received)
            Assert.Null(control.Received.Single()["Sealed"]);

        Assert.Equal(HttpStatusCode.OK, (await server.Admin.PostAsync("api/workers/protection", null)).StatusCode);
        Assert.NotNull(control.Key);
        Assert.True((bool)JObject.Parse(await server.Admin.GetStringAsync("api/workers/protection"))["hasKey"]!);

        var status = JObject.Parse(await server.Admin.GetStringAsync("api/workers/status"));
        Assert.Equal((true, "admin"), ((bool)status["Protected"]!, (string?)status["By"]));
        lock (control.Received)
        {
            // Nothing of what was asked can be read by whoever sees it go by.
            var last = control.Received.Last();
            Assert.NotNull(last["Sealed"]);
            Assert.Null(last["Command"]);
        }

        // Changing the key goes sealed with the one in use, and what comes after with the new one.
        var before = control.Key;
        Assert.Equal(HttpStatusCode.OK, (await server.Admin.PostAsync("api/workers/protection", null)).StatusCode);
        Assert.NotEqual(before, control.Key);
        Assert.Equal(HttpStatusCode.OK, (await server.Admin.GetAsync("api/workers/status")).StatusCode);

        // The answer to "take this key" is lost: the next request tries it, and nobody is locked out.
        control.LostOnce = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await server.Admin.PostAsync("api/workers/protection", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.Admin.GetAsync("api/workers/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.Admin.GetAsync("api/workers/status")).StatusCode);

        // A Worker Control with another key, or that forgot its own, is said in words that tell what to do.
        var mine = control.Key;
        control.Key = new byte[32];
        var refused = await server.Admin.GetAsync("api/workers/status");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Contains("admin.key", (string?)JObject.Parse(await refused.Content.ReadAsStringAsync())["error"]);
        control.Key = null;
        Assert.Contains("Proteja a fila de novo", (string?)JObject.Parse(await (await server.Admin.GetAsync("api/workers/status")).Content.ReadAsStringAsync())["error"]);
        Assert.Equal(HttpStatusCode.OK, (await server.Admin.PostAsync("api/workers/protection", null)).StatusCode);
        Assert.NotNull(control.Key);
        Assert.NotEqual(mine, control.Key);
        Assert.Equal(HttpStatusCode.OK, (await server.Admin.GetAsync("api/workers/status")).StatusCode);
    }

    [Fact]
    public async Task Only_the_master_protects_the_queue()
    {
        await server.Admin.PostAsJsonAsync("api/users", new { login = "keyless", name = "Keyless", password = "a-long-password" });
        using var other = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri($"http://localhost:{server.PanelPort}/") };
        Assert.Equal(HttpStatusCode.OK, (await other.PostAsJsonAsync("api/login", new { user = "keyless", password = "a-long-password" })).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsync("api/workers/protection", null)).StatusCode);
    }
}
