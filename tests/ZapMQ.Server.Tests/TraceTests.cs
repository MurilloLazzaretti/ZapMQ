using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using ZapMQ.Server.Panel;

namespace ZapMQ.Server.Tests;

/// <summary>
/// The trace of a supervised process, watched through the panel. Here the process is a v2
/// client that answers the request the way the worker wrapper does.
/// </summary>
public class TraceTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static int _next = 700000;

    private static int Pid() => Interlocked.Increment(ref _next);

    /// <summary>
    /// Stands for a worker: listens on its trace request queue, answers each request with the
    /// given text and keeps what was asked.
    /// </summary>
    private async Task<(V2Client Client, List<JObject> Asked)> Worker(int pid, string answer = "on")
    {
        var client = await V2Client.ConnectAsync(server.Port, "worker");
        var asked = new List<JObject>();
        await client.RequestAsync("bind", pid + "TR");
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var delivery = await client.NextPushAsync();
                lock (asked)
                    asked.Add((JObject)delivery["message"]!["body"]!);
                await client.RequestAsync("respond", pid + "TR", frame =>
                {
                    frame["messageId"] = delivery["message"]!["id"];
                    frame["response"] = new JObject { ["message"] = answer, ["port"] = 0 };
                });
            }
        });
        return (client, asked);
    }

    /// <summary>
    /// Stands for the Worker Control: answers every administration request with the given
    /// answer and keeps what was asked.
    /// </summary>
    private async Task<(V2Client Client, List<JObject> Asked)> WorkerControl(Func<JObject, JObject> answer)
    {
        var client = await V2Client.ConnectAsync(server.Port, "WorkerControl");
        var asked = new List<JObject>();
        await client.RequestAsync("bind", "WorkerControlAdmin");
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var delivery = await client.NextPushAsync();
                var request = (JObject)delivery["message"]!["body"]!;
                lock (asked)
                    asked.Add(request);
                await client.RequestAsync("respond", "WorkerControlAdmin", frame =>
                {
                    frame["messageId"] = delivery["message"]!["id"];
                    frame["response"] = answer(request);
                });
            }
        });
        return (client, asked);
    }

    private static Task<JObject> Send(V2Client worker, int pid, params string[] lines) => Send(worker, pid, 0, lines);

    private static Task<JObject> Send(V2Client worker, int pid, int dropped, params string[] lines) =>
        worker.RequestAsync("publish", "zapmq.trace." + pid, frame =>
        {
            frame["ttlMs"] = 10000;
            frame["body"] = new JObject
            {
                ["ProcessId"] = pid.ToString(),
                ["Dropped"] = dropped,
                ["Lines"] = new JArray(lines.Select((text, index) => new JObject { ["Seq"] = index + 1, ["At"] = "2026-01-02T03:04:05.678Z", ["Text"] = text }))
            };
        });

    private async Task<Watcher> Watch(int pid)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"api/trace/{pid}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var response = await server.Admin.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        return new Watcher(response, new StreamReader(await response.Content.ReadAsStreamAsync()));
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
            await Task.Delay(25);
        Assert.True(condition());
    }

    /// <summary>
    /// One open request to the trace route, read event by event.
    /// </summary>
    private sealed class Watcher(HttpResponseMessage response, StreamReader reader) : IDisposable
    {
        public async Task<(string Kind, JToken Data)> NextAsync()
        {
            using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string? kind = null;
            while (true)
            {
                var line = await reader.ReadLineAsync(patience.Token) ?? throw new EndOfStreamException();
                if (line.StartsWith("event: "))
                    kind = line[7..];
                else if (line.StartsWith("data: ") && kind is not null)
                    return (kind, JToken.Parse(line[6..]));
            }
        }

        public async Task<JToken> NextAsync(string kind)
        {
            while (true)
            {
                var (found, data) = await NextAsync();
                if (found == kind)
                    return data;
            }
        }

        public async Task<string> StateAsync(params string[] skipping)
        {
            while (true)
            {
                var state = (string)(await NextAsync("state"))["state"]!;
                if (!skipping.Contains(state))
                    return state;
            }
        }

        public void Dispose() => response.Dispose();
    }

    [Fact]
    public async Task Watching_asks_the_process_for_its_trace_and_passes_the_lines_on()
    {
        var pid = Pid();
        var (worker, asked) = await Worker(pid);
        await using var _ = worker;
        using var watcher = await Watch(pid);

        Assert.Equal("on", await watcher.StateAsync("starting"));
        JObject request;
        lock (asked)
            request = Assert.Single(asked);
        Assert.Equal("start zapmq trace", (string)request["message"]!);
        Assert.Equal("zapmq.trace." + pid, (string)request["queue"]!);
        Assert.Equal(30, (int)request["lease"]!);

        await Send(worker, pid, "first line", "segunda, com acentuação");
        var lines = await watcher.NextAsync("lines");

        Assert.Equal(["first line", "segunda, com acentuação"], lines.Select(line => (string)line["text"]!));
        Assert.Equal([1, 2], lines.Select(line => (long)line["n"]!));
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:04:05.678Z"), (DateTimeOffset)lines[0]!["at"]!);
    }

    [Fact]
    public async Task What_the_process_dropped_is_told_before_the_lines_that_follow()
    {
        var pid = Pid();
        var (worker, _) = await Worker(pid);
        await using var __ = worker;
        using var watcher = await Watch(pid);
        Assert.Equal("on", await watcher.StateAsync("starting"));

        await Send(worker, pid, dropped: 7, "after the gap");
        var lines = await watcher.NextAsync("lines");

        Assert.Equal(7, (long)lines[0]!["dropped"]!);
        Assert.Equal("", (string)lines[0]!["text"]!);
        Assert.Equal("after the gap", (string)lines[1]!["text"]!);
    }

    [Fact]
    public async Task A_process_that_does_not_know_this_trace_is_said_so()
    {
        var pid = Pid();
        // What the wrappers before this one answer to a request they do not recognise.
        var (worker, _) = await Worker(pid, answer: "");
        await using var __ = worker;
        using var watcher = await Watch(pid);

        Assert.Equal("unsupported", await watcher.StateAsync("starting"));
    }

    [Fact]
    public async Task A_process_nobody_answers_for_is_unreachable()
    {
        using var watcher = await Watch(Pid());

        Assert.Equal("unreachable", await watcher.StateAsync("starting"));
    }

    [Fact]
    public async Task The_request_is_renewed_while_somebody_watches_and_withdrawn_when_they_leave()
    {
        var hub = server.Services.GetRequiredService<TraceHub>();
        var before = hub.RenewEvery;
        hub.RenewEvery = TimeSpan.FromMilliseconds(150);
        try
        {
            var pid = Pid();
            var (worker, asked) = await Worker(pid);
            await using var _ = worker;
            var watcher = await Watch(pid);
            Assert.Equal("on", await watcher.StateAsync("starting"));

            await Eventually(() => { lock (asked) return asked.Count(request => (string)request["message"]! == "start zapmq trace") >= 3; });

            watcher.Dispose();
            await Eventually(() => { lock (asked) return asked.Any(request => (string)request["message"]! == "stop zapmq trace"); });

            // What still arrives after that is thrown away, and leaves nothing behind.
            await Send(worker, pid, "nobody is watching");
            await Task.Delay(300);
            Assert.Empty(JArray.Parse(await server.Admin.GetStringAsync("admin/dead-letters/zapmq.trace." + pid)));
        }
        finally
        {
            hub.RenewEvery = before;
        }
    }

    [Fact]
    public async Task Whoever_arrives_later_sees_the_lines_from_before()
    {
        var pid = Pid();
        var (worker, _) = await Worker(pid);
        await using var __ = worker;
        using var first = await Watch(pid);
        Assert.Equal("on", await first.StateAsync("starting"));
        await Send(worker, pid, "one", "two");
        await first.NextAsync("lines");

        using var second = await Watch(pid);
        var lines = await second.NextAsync("lines");

        Assert.Equal(["one", "two"], lines.Select(line => (string)line["text"]!));

        await Send(worker, pid, "three");
        Assert.Equal("three", (string)(await first.NextAsync("lines"))[0]!["text"]!);
        Assert.Equal("three", (string)(await second.NextAsync("lines"))[0]!["text"]!);
    }

    [Fact]
    public async Task A_process_that_leaves_ends_its_trace()
    {
        var hub = server.Services.GetRequiredService<TraceHub>();
        var before = hub.RenewEvery;
        hub.RenewEvery = TimeSpan.FromMilliseconds(150);
        try
        {
            var pid = Pid();
            var (worker, _) = await Worker(pid);
            using var watcher = await Watch(pid);
            Assert.Equal("on", await watcher.StateAsync("starting"));

            await worker.DisposeAsync();

            Assert.Equal("ended", await watcher.StateAsync());
        }
        finally
        {
            hub.RenewEvery = before;
        }
    }

    [Fact]
    public async Task A_process_of_the_old_trace_is_asked_for_through_the_worker_control()
    {
        var pid = Pid();
        var (worker, _) = await Worker(pid, answer: "");
        await using var __ = worker;
        var (control, asked) = await WorkerControl(_ => new JObject { ["Ok"] = true });
        await using var ___ = control;
        var watcher = await Watch(pid);

        Assert.Equal("on", await watcher.StateAsync("starting"));
        JObject request;
        lock (asked)
            request = Assert.Single(asked);
        Assert.Equal("StartTrace", (string)request["Command"]!);
        Assert.Equal(pid, (int)request["ProcessId"]!);
        Assert.Equal("zapmq.trace." + pid, (string)request["Queue"]!);

        // What the Worker Control publishes arrives like the lines of any other process.
        await Send(control, pid, "from the socket");
        Assert.Equal("from the socket", (string)(await watcher.NextAsync("lines"))[0]!["text"]!);

        watcher.Dispose();
        await Eventually(() => { lock (asked) return asked.Any(item => (string)item["Command"]! == "StopTrace" && (int)item["ProcessId"]! == pid); });
    }

    [Fact]
    public async Task A_process_that_is_not_on_v2_is_also_asked_for_through_the_worker_control()
    {
        var pid = Pid();
        var (control, asked) = await WorkerControl(_ => new JObject { ["Ok"] = true });
        await using var _ = control;
        using var watcher = await Watch(pid);

        Assert.Equal("on", await watcher.StateAsync("starting"));
        lock (asked)
            Assert.Equal("StartTrace", (string)Assert.Single(asked)["Command"]!);
    }

    [Fact]
    public async Task What_the_worker_control_could_not_do_is_told()
    {
        var pid = Pid();
        var (worker, _) = await Worker(pid, answer: "");
        await using var __ = worker;
        var (control, ___) = await WorkerControl(_ => new JObject
        {
            ["Ok"] = false,
            ["Error"] = new JObject { ["Code"] = "trace-failed", ["Message"] = "The worker did not connect to send its trace" }
        });
        await using var ____ = control;
        using var watcher = await Watch(pid);

        var state = await watcher.NextAsync("state");
        while ((string)state["state"]! == "starting")
            state = await watcher.NextAsync("state");

        Assert.Equal("unsupported", (string)state["state"]!);
        Assert.Contains("The worker did not connect", (string)state["message"]!);
    }

    [Fact]
    public async Task The_relay_ends_when_the_worker_control_no_longer_knows_the_process()
    {
        var hub = server.Services.GetRequiredService<TraceHub>();
        var before = hub.RenewEvery;
        hub.RenewEvery = TimeSpan.FromMilliseconds(150);
        try
        {
            var pid = Pid();
            var calls = 0;
            var (control, _) = await WorkerControl(_ => Interlocked.Increment(ref calls) == 1
                ? new JObject { ["Ok"] = true }
                : new JObject { ["Ok"] = false, ["Error"] = new JObject { ["Code"] = "not-found", ["Message"] = "There is no worker with that process id" } });
            await using var __ = control;
            using var watcher = await Watch(pid);

            Assert.Equal("on", await watcher.StateAsync("starting"));
            Assert.Equal("ended", await watcher.StateAsync());
        }
        finally
        {
            hub.RenewEvery = before;
        }
    }
}
