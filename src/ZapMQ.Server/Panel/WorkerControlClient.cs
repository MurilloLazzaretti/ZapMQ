using System.Text.Json;
using System.Text.Json.Nodes;
using ZapMQ.Core;

namespace ZapMQ.Server.Panel;

/// <summary>
/// What the Worker Control answered, or why there is no answer.
/// </summary>
public sealed record WorkerControlAnswer(bool Reached, JsonNode? Body, string? Problem)
{
    public bool Ok => Reached && Body?["Ok"]?.GetValue<bool>() == true;

    public string? ErrorCode => Body?["Error"]?["Code"]?.GetValue<string>();

    public string? ErrorMessage => Body?["Error"]?["Message"]?.GetValue<string>() ?? Problem;
}

/// <summary>
/// Talks to the Worker Control the way anybody else would: RPC messages on its administration
/// queue. It is published from inside the broker, so the panel needs no connection of its own
/// and the Worker Control may be on another machine.
/// </summary>
public sealed class WorkerControlClient(Broker broker, TimeProvider time)
{
    public const string Queue = "WorkerControlAdmin";
    public const int ContractVersion = 1;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(8);

    /// <summary>
    /// True while somebody is consuming the administration queue over v2, which is how the
    /// Worker Control 2.0 does it.
    /// </summary>
    public bool IsConnected => broker.GetQueue(Queue) is { Consumers: > 0 };

    public async Task<WorkerControlAnswer> AskAsync(string command, string? by, Action<JsonObject>? more = null, CancellationToken cancellation = default)
    {
        // Asking a queue nobody consumes would only leave a dead letter behind each time.
        if (!IsConnected)
            return new WorkerControlAnswer(false, null, "O Worker Control não está conectado ao ZapMQ, ou é uma versão anterior à 2.0.");

        var request = new JsonObject { ["Command"] = command, ["Version"] = ContractVersion };
        if (!string.IsNullOrEmpty(by))
            request["By"] = by;
        more?.Invoke(request);

        // Each question has a receiver of its own, so the answer needs no id to be matched and
        // cannot arrive before somebody is waiting for it.
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        broker.Publish(Queue, request.ToJsonString(), rpc: true, ttl: Patience, replyTo: new Receiver(answer), publisher: "panel");

        try
        {
            var response = await answer.Task.WaitAsync(Patience, time, cancellation);
            return response is null
                ? new WorkerControlAnswer(false, null, "O Worker Control não soube responder a esse pedido.")
                : new WorkerControlAnswer(true, JsonNode.Parse(response), null);
        }
        catch (TimeoutException)
        {
            return new WorkerControlAnswer(false, null, "O Worker Control não respondeu a tempo.");
        }
        catch (JsonException)
        {
            return new WorkerControlAnswer(false, null, "O Worker Control respondeu algo que não foi possível ler.");
        }
    }

    /// <summary>
    /// Where the broker pushes the answer of one question.
    /// </summary>
    private sealed class Receiver(TaskCompletionSource<string?> answer) : Consumer
    {
        public override string Description => "ZapMQ panel";

        protected override void Deliver(BrokerMessage message)
        {
        }

        protected override void DeliverResponse(BrokerMessage message) => answer.TrySetResult(message.Response);
    }
}
