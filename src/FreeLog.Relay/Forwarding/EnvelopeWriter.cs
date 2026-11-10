using System.Text.Json;
using System.Text.Json.Nodes;
using FreeLog.Relay.Envelopes;

namespace FreeLog.Relay.Forwarding;

public static class EnvelopeWriter
{
    /// <summary>
    /// Serializes an envelope from a header and a subset of items. Item headers and payload bytes are kept as-is,
    /// so an explicit <c>length</c> stays valid. A <c>dsn</c> in the envelope header is replaced with the forward
    /// target's DSN, because the receiver may check it against the project it authenticates.
    /// </summary>
    public static byte[] Write(JsonObject header, IEnumerable<EnvelopeItem> items, string? replacementDsn)
    {
        var newHeader = (JsonObject)header.DeepClone();
        if (replacementDsn is not null && newHeader.ContainsKey("dsn"))
            newHeader["dsn"] = replacementDsn;

        using var ms = new MemoryStream();
        WriteJsonLine(ms, newHeader);
        foreach (var item in items)
        {
            WriteJsonLine(ms, item.Header);
            ms.Write(item.Payload.Span);
            ms.WriteByte((byte)'\n');
        }

        return ms.ToArray();
    }

    private static void WriteJsonLine(Stream stream, JsonNode node)
    {
        using (var writer = new Utf8JsonWriter(stream))
            node.WriteTo(writer);
        stream.WriteByte((byte)'\n');
    }
}
