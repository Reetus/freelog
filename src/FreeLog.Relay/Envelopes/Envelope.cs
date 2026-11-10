using System.Text.Json;
using System.Text.Json.Nodes;

namespace FreeLog.Relay.Envelopes;

public sealed record EnvelopeItem(string Type, JsonObject Header, ReadOnlyMemory<byte> Payload);

public sealed record Envelope(JsonObject Header, IReadOnlyList<EnvelopeItem> Items);

public sealed class EnvelopeFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Parses the Sentry envelope format: newline-delimited JSON with an envelope header,
/// then pairs of (item header, item payload). An item header may declare an explicit
/// <c>length</c> in bytes; otherwise the payload runs to the next newline.
/// See https://develop.sentry.dev/sdk/data-model/envelopes/
/// </summary>
public static class EnvelopeParser
{
    public static Envelope Parse(ReadOnlyMemory<byte> data)
    {
        var pos = 0;
        var headerLine = ReadLine(data, ref pos);
        var header = ParseObject(headerLine, "envelope header");
        var items = new List<EnvelopeItem>();

        while (pos < data.Length)
        {
            var itemHeaderLine = ReadLine(data, ref pos);
            if (IsBlank(itemHeaderLine.Span))
                continue;

            var itemHeader = ParseObject(itemHeaderLine, "item header");
            var type = itemHeader["type"]?.GetValueKind() == JsonValueKind.String
                ? itemHeader["type"]!.GetValue<string>()
                : throw new EnvelopeFormatException("Item header is missing 'type'.");

            ReadOnlyMemory<byte> payload;
            if (itemHeader["length"] is JsonValue lengthNode && lengthNode.TryGetValue<long>(out var length))
            {
                if (length < 0 || pos + length > data.Length)
                    throw new EnvelopeFormatException($"Item '{type}' declares length {length} beyond end of envelope.");
                payload = data.Slice(pos, (int)length);
                pos += (int)length;
                if (pos < data.Length && data.Span[pos] == (byte)'\n')
                    pos++;
            }
            else
            {
                payload = ReadLine(data, ref pos);
            }

            items.Add(new EnvelopeItem(type, itemHeader, payload));
        }

        return new Envelope(header, items);
    }

    private static ReadOnlyMemory<byte> ReadLine(ReadOnlyMemory<byte> data, ref int pos)
    {
        var rest = data.Span[pos..];
        var nl = rest.IndexOf((byte)'\n');
        ReadOnlyMemory<byte> line;
        if (nl < 0)
        {
            line = data[pos..];
            pos = data.Length;
        }
        else
        {
            line = data.Slice(pos, nl);
            pos += nl + 1;
        }

        if (line.Length > 0 && line.Span[^1] == (byte)'\r')
            line = line[..^1];
        return line;
    }

    private static bool IsBlank(ReadOnlySpan<byte> s)
    {
        foreach (var b in s)
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r'))
                return false;
        return true;
    }

    private static JsonObject ParseObject(ReadOnlyMemory<byte> json, string what)
    {
        if (IsBlank(json.Span))
            return new JsonObject();
        try
        {
            return JsonNode.Parse(json.Span) as JsonObject
                   ?? throw new EnvelopeFormatException($"The {what} is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new EnvelopeFormatException($"The {what} is not valid JSON.", ex);
        }
    }
}
