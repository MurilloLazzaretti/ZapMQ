using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using ZapMQ.Core;

namespace ZapMQ.Server.V2;

/// <summary>
/// The frames the server sends. Bodies and responses are stored as JSON text and are written
/// into the frame as they are.
/// </summary>
internal static class V2Frames
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static byte[] Ok(long re, Action<Utf8JsonWriter>? more = null) => Write(writer =>
    {
        writer.WriteNumber("re", re);
        writer.WriteBoolean("ok", true);
        more?.Invoke(writer);
    });

    public static byte[] Error(long? re, string code, string message) => Write(writer =>
    {
        if (re is { } id)
            writer.WriteNumber("re", id);
        writer.WriteBoolean("ok", false);
        writer.WriteStartObject("error");
        writer.WriteString("code", code);
        writer.WriteString("message", message);
        writer.WriteEndObject();
    });

    public static byte[] Deliver(BrokerMessage message) => Write(writer =>
    {
        writer.WriteString("op", "deliver");
        writer.WriteString("queue", message.Queue);
        WriteMessage(writer, message);
    });

    public static byte[] Response(BrokerMessage message) => Write(writer =>
    {
        writer.WriteString("op", "response");
        writer.WriteString("queue", message.Queue);
        writer.WriteString("messageId", message.Id);
        WriteMessage(writer, message);
    });

    public static byte[] Bye(string reason) => Write(writer =>
    {
        writer.WriteString("op", "bye");
        writer.WriteString("reason", reason);
    });

    private static void WriteMessage(Utf8JsonWriter writer, BrokerMessage message)
    {
        writer.WriteStartObject("message");
        writer.WriteString("id", message.Id);
        writer.WritePropertyName("body");
        writer.WriteRawValue(message.Body, skipInputValidation: true);
        writer.WriteBoolean("rpc", message.Rpc);
        if (message.Response is not null)
        {
            writer.WritePropertyName("response");
            writer.WriteRawValue(message.Response, skipInputValidation: true);
        }
        if (message.RequeuedFrom is not null)
            writer.WriteString("requeuedFrom", message.RequeuedFrom);
        writer.WriteEndObject();
    }

    private static byte[] Write(Action<Utf8JsonWriter> content)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            content(writer);
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
}
