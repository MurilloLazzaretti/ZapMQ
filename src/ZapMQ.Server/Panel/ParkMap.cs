using System.Text.RegularExpressions;
using ZapMQ.Core;
using ZapMQ.Server.V2;

namespace ZapMQ.Server.Panel;

/// <summary>
/// Everything that talks through the broker, as a drawing: the applications, the queues and
/// who publishes in and consumes from which.
/// </summary>
public static partial class ParkMap
{
    /// <summary>
    /// Queues that carry no work: the ones each supervised process has for its keep-alive,
    /// safe stop and trace (named after its process id), and the ones the Worker Control and
    /// the wrappers use for themselves. They are counted on the application, not drawn.
    /// </summary>
    public static bool IsInternal(string queue) =>
        ProcessQueue().IsMatch(queue)
        || queue == WorkerControlClient.Queue
        || queue == "CurrentWorkers"
        || queue == "ReloadConfig"
        || queue.StartsWith("WorkerControl.", StringComparison.Ordinal)
        || queue.StartsWith("zapmq.", StringComparison.Ordinal);

    [GeneratedRegex(@"^\d+(SS|TR)?$")]
    private static partial Regex ProcessQueue();

    public static object Build(Broker broker, V2Connections connections, TimeSpan window)
    {
        var applications = new SortedDictionary<string, Application>(StringComparer.OrdinalIgnoreCase);
        var links = new SortedDictionary<(string Application, string Queue, string Kind), Link>();
        var drawn = new HashSet<string>(StringComparer.Ordinal);

        Application Of(string protocol, string name)
        {
            var key = protocol + ":" + name;
            if (!applications.TryGetValue(key, out var application))
                applications[key] = application = new Application(key, protocol, name);
            return application;
        }

        void Join(Application application, string queue, string kind, long count, DateTimeOffset? lastSeen, bool bound)
        {
            drawn.Add(queue);
            var key = (application.Id, queue, kind);
            if (!links.TryGetValue(key, out var link))
                links[key] = link = new Link();
            link.Count += count;
            link.Bound |= bound;
            if (lastSeen is not null && (link.LastSeen is null || lastSeen > link.LastSeen))
                link.LastSeen = lastSeen;
        }

        // Who is connected now, and what each one consumes.
        foreach (var connection in connections.All())
        {
            var application = Of("v2", connection.ClientName);
            var instance = application.Instance(connection.Host, connection.ProcessId);
            instance.Connections++;
            instance.Busy |= connection.IsBusy;
            foreach (var queue in connection.GetBoundQueues())
            {
                if (IsInternal(queue))
                    application.InternalQueues.Add(queue);
                else
                    Join(application, queue, "consume", 0, null, bound: true);
            }
        }

        // Who published and who came asking, lately. An application that has gone is still
        // drawn while what it did is inside the window.
        foreach (var activity in broker.GetActivity(window))
        {
            if (IsInternal(activity.Queue))
                continue;
            foreach (var (party, kind) in activity.Publishers.Select(party => (party, "publish")).Concat(activity.Askers.Select(party => (party, "consume"))))
            {
                Application application;
                if (party.Name.StartsWith("v2:", StringComparison.Ordinal) && party.Name[3..].Split('|') is [_, var name, _, _])
                    application = Of("v2", name);
                else if (party.Name.StartsWith("v1:", StringComparison.Ordinal))
                    application = Of("v1", party.Name[3..]);
                else
                    continue;

                if (application.LastSeen is null || party.LastSeen > application.LastSeen)
                    application.LastSeen = party.LastSeen;
                Join(application, activity.Queue, kind, party.Count, party.LastSeen, bound: false);
            }
        }

        var dead = broker.GetDeadLetterSummary().ToDictionary(item => item.Queue, StringComparer.Ordinal);
        var queues = broker.GetQueues()
            .Where(queue => !IsInternal(queue.Name))
            .Select(queue =>
            {
                drawn.Remove(queue.Name);
                dead.TryGetValue(queue.Name, out var letters);
                return (object)new
                {
                    name = queue.Name,
                    exists = true,
                    pending = queue.Pending,
                    processing = queue.Processing,
                    consumers = queue.Consumers,
                    paused = queue.Paused,
                    published = queue.Published,
                    delivered = queue.Delivered,
                    deadLetters = (letters?.Expired ?? 0) + (letters?.NotConsumed ?? 0) + (letters?.Unconfirmed ?? 0)
                };
            })
            .ToList();

        // A queue somebody is bound to exists; one only remembered by a link may have been let go.
        queues.AddRange(drawn.Order(StringComparer.Ordinal).Select(name => (object)new
        {
            name,
            exists = false,
            pending = 0,
            processing = 0,
            consumers = 0,
            paused = false,
            published = 0L,
            delivered = 0L,
            deadLetters = 0
        }));

        return new
        {
            windowMinutes = (int)window.TotalMinutes,
            applications = applications.Values.Select(application => new
            {
                id = application.Id,
                protocol = application.Protocol,
                name = application.Name,
                lastSeen = application.LastSeen,
                internalQueues = application.InternalQueues.Count,
                instances = application.Instances.Values.Select(instance => new
                {
                    host = instance.Host,
                    pid = instance.ProcessId,
                    connections = instance.Connections,
                    busy = instance.Busy
                })
            }),
            queues,
            links = links.Select(pair => new
            {
                application = pair.Key.Application,
                queue = pair.Key.Queue,
                kind = pair.Key.Kind,
                count = pair.Value.Count,
                lastSeen = pair.Value.LastSeen,
                bound = pair.Value.Bound
            })
        };
    }

    private sealed class Application(string id, string protocol, string name)
    {
        public string Id => id;

        public string Protocol => protocol;

        public string Name => name;

        public DateTimeOffset? LastSeen { get; set; }

        public HashSet<string> InternalQueues { get; } = new(StringComparer.Ordinal);

        public SortedDictionary<(string Host, int ProcessId), Instance> Instances { get; } = [];

        public Instance Instance(string host, int processId)
        {
            if (!Instances.TryGetValue((host, processId), out var instance))
                Instances[(host, processId)] = instance = new Instance(host, processId);
            return instance;
        }
    }

    private sealed class Instance(string host, int processId)
    {
        public string Host => host;

        public int ProcessId => processId;

        public int Connections { get; set; }

        public bool Busy { get; set; }
    }

    private sealed class Link
    {
        public long Count { get; set; }

        public DateTimeOffset? LastSeen { get; set; }

        public bool Bound { get; set; }
    }
}
