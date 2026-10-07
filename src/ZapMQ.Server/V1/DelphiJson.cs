using System.Text;
using System.Text.Json;

namespace ZapMQ.Server.V1;

/// <summary>
/// Writes JSON with the same bytes the Delphi 1.x server produces: compact, numbers kept as
/// they were sent, and <c>/</c> escaped as <c>\/</c> inside strings.
/// </summary>
internal static class DelphiJson
{
    public static string Write(JsonElement element)
    {
        var json = new StringBuilder();
        Write(json, element);
        return json.ToString();
    }

    public static void Write(StringBuilder json, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                json.Append('{');
                var firstProperty = true;
                foreach (var property in element.EnumerateObject())
                {
                    if (!firstProperty)
                        json.Append(',');
                    firstProperty = false;
                    WriteString(json, property.Name, escapeNonAscii: false);
                    json.Append(':');
                    Write(json, property.Value);
                }
                json.Append('}');
                break;

            case JsonValueKind.Array:
                json.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem)
                        json.Append(',');
                    firstItem = false;
                    Write(json, item);
                }
                json.Append(']');
                break;

            case JsonValueKind.String:
                WriteString(json, element.GetString()!, escapeNonAscii: false);
                break;

            default:
                json.Append(element.GetRawText());
                break;
        }
    }

    /// <summary>
    /// Writes a quoted string. DataSnap additionally turns every non-ASCII character of a method
    /// result into a <c>\uXXXX</c> escape, which is what <paramref name="escapeNonAscii"/> is for.
    /// </summary>
    public static void WriteString(StringBuilder json, string text, bool escapeNonAscii)
    {
        json.Append('"');
        foreach (var character in text)
        {
            switch (character)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                case '/': json.Append("\\/"); break;
                case '\b': json.Append("\\b"); break;
                case '\t': json.Append("\\t"); break;
                case '\n': json.Append("\\n"); break;
                case '\f': json.Append("\\f"); break;
                case '\r': json.Append("\\r"); break;
                default:
                    if (character < ' ' || (escapeNonAscii && character > '~'))
                        json.Append("\\u").Append(((int)character).ToString("X4"));
                    else
                        json.Append(character);
                    break;
            }
        }
        json.Append('"');
    }
}
