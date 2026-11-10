using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FreeLog.Relay.Logs;

/// <summary>
/// Maps Sentry log entries (payload of an envelope item of type <c>log</c>) to flat
/// VictoriaLogs JSON-line records. Field names match what Vector produces for container
/// logs so both ingestion paths can be queried the same way.
/// </summary>
public static class LogRecordMapper
{
    // Fields we set ourselves; attributes with these names are kept under "attr.<name>".
    private static readonly HashSet<string> Reserved =
        ["_time", "_msg", "_stream", "level", "severity_number", "trace_id", "span_id", "service", "env", "project", "source"];

    // Sentry attribute → freelog field.
    private static readonly Dictionary<string, string> Renames = new()
    {
        ["sentry.environment"] = "env",
        ["sentry.release"] = "release",
        ["sentry.message.template"] = "message_template",
        ["service.name"] = "service",
    };

    public static IEnumerable<JsonObject> MapLogItem(ReadOnlyMemory<byte> payload, ProjectOptions project)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(payload.Span);
        }
        catch (JsonException ex)
        {
            throw new Envelopes.EnvelopeFormatException("Log item payload is not valid JSON.", ex);
        }

        if (root?["items"] is not JsonArray items)
            yield break;

        foreach (var node in items)
        {
            if (node is JsonObject entry)
                yield return Map(entry, project);
        }
    }

    public static JsonObject Map(JsonObject entry, ProjectOptions project)
    {
        var record = new JsonObject
        {
            ["_time"] = FormatTimestamp(entry["timestamp"]),
            ["_msg"] = AsString(entry["body"]) ?? "",
            ["level"] = NormalizeLevel(AsString(entry["level"])),
            ["service"] = project.Service,
            ["project"] = project.Id,
            ["source"] = "sentry-sdk",
        };

        if (project.Environment is not null)
            record["env"] = project.Environment;
        if (entry["severity_number"] is JsonValue sev && sev.TryGetValue<int>(out var sevNum))
            record["severity_number"] = sevNum;
        if (AsString(entry["trace_id"]) is { Length: > 0 } traceId)
            record["trace_id"] = traceId;
        if (AsString(entry["span_id"]) is { Length: > 0 } spanId)
            record["span_id"] = spanId;

        if (entry["attributes"] is JsonObject attributes)
        {
            foreach (var (name, raw) in attributes)
            {
                var value = UnwrapAttribute(raw);
                if (value is null)
                    continue;

                if (Renames.TryGetValue(name, out var renamed))
                    record[renamed] = value;
                else if (Reserved.Contains(name))
                    record["attr." + name] = value;
                else
                    record[name] = value;
            }
        }

        return record;
    }

    /// <summary>Attributes arrive as <c>{"value": x, "type": "string|integer|double|boolean"}</c>.</summary>
    private static JsonNode? UnwrapAttribute(JsonNode? raw) => raw switch
    {
        JsonObject o when o.ContainsKey("value") => o["value"]?.DeepClone(),
        null => null,
        _ => raw.DeepClone(),
    };

    private static string FormatTimestamp(JsonNode? node)
    {
        DateTimeOffset ts;
        if (node is JsonValue v && v.TryGetValue<double>(out var seconds) && double.IsFinite(seconds) && seconds > 0)
        {
            ts = DateTimeOffset.UnixEpoch.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
        }
        else if (node is JsonValue s && s.TryGetValue<string>(out var str) &&
                 DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            ts = parsed;
        }
        else
        {
            ts = DateTimeOffset.UtcNow;
        }

        return ts.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>Canonical levels used across freelog: trace, debug, info, warn, error, fatal.</summary>
    public static string NormalizeLevel(string? level) => level?.Trim().ToLowerInvariant() switch
    {
        "trace" or "verbose" or "vrb" => "trace",
        "debug" or "dbg" => "debug",
        null or "" or "info" or "information" or "inf" or "notice" => "info",
        "warn" or "warning" or "wrn" => "warn",
        "error" or "err" or "eror" => "error",
        "fatal" or "critical" or "crit" or "ftl" or "panic" => "fatal",
        var other => other,
    };

    private static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
