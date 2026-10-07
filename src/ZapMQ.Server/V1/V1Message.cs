using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ZapMQ.Core;

namespace ZapMQ.Server.V1;

/// <summary>
/// The 1.x message envelope: <c>Id</c>, <c>Body</c>, <c>RPC</c>, <c>TTL</c> and <c>Response</c>.
/// </summary>
internal static class V1Message
{
    private const string EmptyObject = "{}";

    // Text is written as it came in instead of as \uXXXX escapes, like the 1.x server did.
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public sealed record Publication(string Body, bool Rpc, TimeSpan Ttl);

    /// <summary>
    /// Reads what a client sends to <c>UpdateMessage</c>. Anything that is not a JSON object is
    /// rejected; a <c>Body</c> that is not an object becomes an empty one, as in 1.x.
    /// </summary>
    public static Publication ParsePublication(string json)
    {
        using var document = ParseObject(json) ?? throw new V1Exception("Invalid JSON format");
        var root = document.RootElement;

        var body = root.TryGetProperty("Body", out var bodyElement) && bodyElement.ValueKind == JsonValueKind.Object
            ? Compact(bodyElement)
            : EmptyObject;
        var rpc = root.TryGetProperty("RPC", out var rpcElement) && rpcElement.ValueKind == JsonValueKind.True;
        var ttl = root.TryGetProperty("TTL", out var ttlElement) && ttlElement.ValueKind == JsonValueKind.Number
                  && ttlElement.TryGetInt64(out var milliseconds) && milliseconds > 0
            ? TimeSpan.FromMilliseconds(milliseconds)
            : TimeSpan.Zero;

        return new Publication(body, rpc, ttl);
    }

    /// <summary>
    /// Reads an RPC response. Returns null when the text is not a JSON object.
    /// </summary>
    public static string? ParseResponse(string json)
    {
        using var document = ParseObject(json);
        return document is null ? null : Compact(document.RootElement);
    }

    public static string Serialize(BrokerMessage message)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("Id", message.Id);
            writer.WritePropertyName("Body");
            writer.WriteRawValue(message.Body, skipInputValidation: true);
            writer.WriteBoolean("RPC", message.Rpc);
            // 1.x never echoed the TTL back; it always answered zero.
            writer.WriteNumber("TTL", 0);
            writer.WritePropertyName("Response");
            writer.WriteRawValue(message.Response ?? EmptyObject, skipInputValidation: true);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Wraps a method result the way DataSnap REST does: <c>{"result":["..."]}</c>.
    /// </summary>
    public static byte[] Envelope(string result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("result");
            writer.WriteStringValue(result);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] Error(string message)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("error", message);
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static JsonDocument? ParseObject(string json)
    {
        try
        {
            var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return document;

            document.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Compact(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            element.WriteTo(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

internal sealed class V1Exception(string message) : Exception(message);
