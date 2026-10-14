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
public sealed class WorkerControlClient(Broker broker, TimeProvider time, AdminKeyStore keys)
{
    public const string Queue = "WorkerControlAdmin";
    public const int ContractVersion = 1;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(8);

    /// <summary>
    /// True while somebody is consuming the administration queue over v2, which is how the
    /// Worker Control 2.0 does it.
    /// </summary>
    public bool IsConnected => broker.GetQueue(Queue) is { Consumers: > 0 };

    public Task<WorkerControlAnswer> AskAsync(string command, string? by, Action<JsonObject>? more = null, CancellationToken cancellation = default) =>
        AskAsync(command, by, Patience, more, cancellation);

    /// <summary>
    /// Asks and waits for as long as said: what changes something on the machine may take a while.
    /// </summary>
    /// <summary>
    /// True while this ZapMQ has a key to seal what it asks with.
    /// </summary>
    public bool HasKey => keys.Key is not null;

    public async Task<WorkerControlAnswer> AskAsync(string command, string? by, TimeSpan patience, Action<JsonObject>? more = null, CancellationToken cancellation = default)
    {
        // Asking a queue nobody consumes would only leave a dead letter behind each time.
        if (!IsConnected)
            return new WorkerControlAnswer(false, null, "O Worker Control não está conectado ao ZapMQ, ou é uma versão anterior à 2.0.");

        var request = new JsonObject { ["Command"] = command, ["Version"] = ContractVersion };
        if (!string.IsNullOrEmpty(by))
            request["By"] = by;
        more?.Invoke(request);

        var answer = await SendAsync(request, keys.Key, patience, cancellation);
        // A key that was sent and whose answer never came may be the one the Worker Control has.
        if (answer is { Reached: true, ErrorCode: "unauthenticated" } && keys.Pending is { } pending)
        {
            var again = await SendAsync(request, pending, patience, cancellation);
            if (again.Reached && again.ErrorCode is not ("unauthenticated" or "no-key"))
            {
                keys.Confirm(pending);
                return again;
            }
        }
        return answer.ErrorCode switch
        {
            // Not an answer to what was asked: said in the words of the panel, as when nobody answers.
            "unauthenticated" => new WorkerControlAnswer(false, null, keys.Key is null
                ? "O Worker Control só aceita pedidos autenticados, e este ZapMQ não tem a chave dele. Se a chave se perdeu, apague o arquivo admin.key na pasta de dados do Worker Control, reinicie-o e proteja a fila de novo pelo painel."
                : "O Worker Control não reconhece a chave deste ZapMQ. Se ela foi trocada por outro ZapMQ, apague o arquivo admin.key na pasta de dados do Worker Control, reinicie-o e proteja a fila de novo pelo painel."),
            "no-key" => new WorkerControlAnswer(false, null, "O Worker Control não tem mais a chave de administração que este ZapMQ usa. Proteja a fila de novo, em Processos e serviços."),
            _ => answer
        };
    }

    /// <summary>
    /// Makes a key, hands it to the Worker Control and, once it says it took it, seals with it
    /// from then on. With a key already in use, the new one is sent sealed with the old.
    /// </summary>
    public async Task<WorkerControlAnswer> ProtectAsync(string? by, CancellationToken cancellation = default)
    {
        if (!IsConnected)
            return new WorkerControlAnswer(false, null, "O Worker Control não está conectado ao ZapMQ, ou é uma versão anterior à 2.0.");
        var current = keys.Key;
        var proposed = keys.Propose();
        JsonObject Request()
        {
            var request = new JsonObject { ["Command"] = "SetAdminKey", ["Version"] = ContractVersion, ["Key"] = Convert.ToBase64String(proposed) };
            if (!string.IsNullOrEmpty(by))
                request["By"] = by;
            return request;
        }

        var answer = await SendAsync(Request(), current, Patience, cancellation);
        // The Worker Control lost its key: it takes a new one as it did the first.
        if (answer is { Reached: true, ErrorCode: "no-key" })
            answer = await SendAsync(Request(), null, Patience, cancellation);
        if (answer.Ok)
            keys.Confirm(proposed);
        else if (answer.Reached)
            keys.Withdraw();
        return answer;
    }

    private async Task<WorkerControlAnswer> SendAsync(JsonObject request, byte[]? key, TimeSpan patience, CancellationToken cancellation)
    {
        // Sealed, nobody else reads it, changes it or sends it again; the answer comes back sealed and tied to the question.
        var id = Guid.NewGuid().ToString("N");
        var sent = key is null
            ? request.ToJsonString()
            : new JsonObject
            {
                ["Sealed"] = AdminSeal.Seal(key, new JsonObject { ["At"] = time.GetUtcNow().ToUnixTimeMilliseconds(), ["Id"] = id, ["Request"] = request.DeepClone() }.ToJsonString())
            }.ToJsonString();

        // Each question has a receiver of its own, so the answer needs no id to be matched and
        // cannot arrive before somebody is waiting for it.
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        broker.Publish(Queue, sent, rpc: true, ttl: patience, replyTo: new Receiver(answer), publisher: "panel");

        try
        {
            var response = await answer.Task.WaitAsync(patience, time, cancellation);
            if (response is null)
                return new WorkerControlAnswer(false, null, "O Worker Control não soube responder a esse pedido.");
            var body = JsonNode.Parse(response);
            if (key is null)
                return new WorkerControlAnswer(true, body, null);
            // A refusal comes as it is; anything else has to be sealed, and be the answer to this question.
            if (body?["Sealed"]?.GetValue<string>() is not { } sealedAnswer)
                return body?["Ok"]?.GetValue<bool>() == false
                    ? new WorkerControlAnswer(true, body, null)
                    : new WorkerControlAnswer(false, null, "A resposta não veio autenticada pelo Worker Control e foi descartada.");
            if (AdminSeal.Open(key, sealedAnswer) is not { } plain || JsonNode.Parse(plain) is not { } opened || opened["Id"]?.GetValue<string>() != id)
                return new WorkerControlAnswer(false, null, "A resposta não veio autenticada pelo Worker Control e foi descartada.");
            return new WorkerControlAnswer(true, opened["Answer"], null);
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
