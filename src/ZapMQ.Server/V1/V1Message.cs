using System.Globalization;
using System.Text;
using System.Text.Json;
using ZapMQ.Core;

namespace ZapMQ.Server.V1;

/// <summary>
/// The 1.x message envelope: <c>Id</c>, <c>Body</c>, <c>RPC</c>, <c>TTL</c> and <c>Response</c>.
/// Validation and error texts follow what the 1.x server answers (see tests/contract).
/// </summary>
internal static class V1Message
{
    private const string EmptyObject = "{}";
    private const string InvalidJson = "Invalid JSON format";
    private const string NotAnObject = "Invalid class typecast";

    public sealed record Publication(string Body, bool Rpc, TimeSpan Ttl);

    public enum ResponseKind { Object, NotJson, NotAnObject }

    /// <summary>
    /// Reads what a client sends to <c>UpdateMessage</c>. <c>Id</c>, <c>RPC</c> and <c>TTL</c>
    /// must be present, although the id is always replaced by one the server generates.
    /// </summary>
    public static Publication ParsePublication(string json)
    {
        using var document = Parse(json) ?? throw new V1Exception(InvalidJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new V1Exception(NotAnObject);

        Required(root, "Id");
        var rpc = Required(root, "RPC").ValueKind == JsonValueKind.True;
        var ttl = ReadTtl(Required(root, "TTL"));

        // A Body that is missing or is not an object becomes an empty one.
        var body = root.TryGetProperty("Body", out var bodyElement) && bodyElement.ValueKind == JsonValueKind.Object
            ? DelphiJson.Write(bodyElement)
            : EmptyObject;

        return new Publication(body, rpc, ttl);
    }

    /// <summary>
    /// Reads an RPC response, telling apart text that is not JSON from JSON that is not an object:
    /// the 1.x server ignores the first and fails on the second.
    /// </summary>
    public static ResponseKind ParseResponse(string json, out string response)
    {
        response = EmptyObject;
        using var document = Parse(json);
        if (document is null)
            return ResponseKind.NotJson;
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return ResponseKind.NotAnObject;

        response = DelphiJson.Write(document.RootElement);
        return ResponseKind.Object;
    }

    public static V1Exception NotAnObjectError() => new(NotAnObject);

    public static string Serialize(BrokerMessage message)
    {
        var json = new StringBuilder();
        json.Append("{\"Id\":");
        DelphiJson.WriteString(json, message.Id, escapeNonAscii: false);
        json.Append(",\"Body\":").Append(message.Body);
        json.Append(",\"RPC\":").Append(message.Rpc ? "true" : "false");
        // 1.x never echoes the TTL back; it always answers zero.
        json.Append(",\"TTL\":0");
        json.Append(",\"Response\":").Append(message.Response ?? EmptyObject);
        json.Append('}');
        return json.ToString();
    }

    /// <summary>
    /// Wraps a method result the way DataSnap REST does: <c>{"result":["..."]}</c>.
    /// </summary>
    public static byte[] Envelope(string result)
    {
        var json = new StringBuilder("{\"result\":[");
        DelphiJson.WriteString(json, result, escapeNonAscii: true);
        json.Append("]}");
        return Encoding.UTF8.GetBytes(json.ToString());
    }

    public static byte[] Error(string message)
    {
        var json = new StringBuilder("{\"error\":");
        DelphiJson.WriteString(json, message, escapeNonAscii: true);
        json.Append('}');
        return Encoding.UTF8.GetBytes(json.ToString());
    }

    private static JsonElement Required(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value : throw new V1Exception($"Value '{name}' not found");

    /// <summary>
    /// Milliseconds as a whole, non-negative number. The 1.x server also refused anything above
    /// 65535; that limit was dropped on purpose.
    /// </summary>
    private static TimeSpan ReadTtl(JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString()!,
            _ => "0"
        };

        if (!uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds))
            throw new V1Exception($"'{text}' is not a valid integer value");

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static JsonDocument? Parse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal sealed class V1Exception(string message) : Exception(message);
