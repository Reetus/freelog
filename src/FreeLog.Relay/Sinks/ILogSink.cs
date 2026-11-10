using System.Text.Json.Nodes;

namespace FreeLog.Relay.Sinks;

public interface ILogSink
{
    /// <summary>Writes one batch to the given target. Throws on failure so the caller can retry.</summary>
    Task WriteAsync(string target, IReadOnlyList<JsonObject> records, CancellationToken ct);
}
