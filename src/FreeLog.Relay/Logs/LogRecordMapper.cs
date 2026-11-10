using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FreeLog.Relay.Logs;

/// <summary>
/// Maps Sentry log entries (envelope item type <c>log</c>) and trace-connected metrics (<c>trace_metric</c>)
/// to flat VictoriaLogs JSON-line records. Log field names match what Vector produces for container logs,
/// so both ingestion paths can be queried the same way. Metrics are stored as one record per emitted value
/// with <c>kind=metric</c> and aggregated at query time (<c>| stats sum(metric.value)</c>).
/// </summary>
public static class LogRecordMapper
{
    // Fields we set ourselves; attributes with these names are kept under "attr.<name>".
    private static readonly HashSet<string> Reserved =
        ["_time", "_msg", "_stream", "level", "severity_number", "trace_id", "span_id", "service", "env", "project", "source",
         "kind", "metric.name", "metric.type", "metric.value", "metric.unit"];

    // Sentry attribute → freelog field.
    private static readonly Dictionary<string, string> Renames = new()
    {
        ["sentry.environment"] = "env",
        ["sentry.release"] = "release",
        ["sentry.message.template"] = "message_template",
        ["service.name"] = "service",
    };

    public static IEnumerable<JsonObject> MapLogItem(ReadOnlyMemory<byte> payload, ProjectOptions project) =>
        MapItems(payload, project, "Log", Map);

    public static IEnumerable<JsonObject> MapMetricItem(ReadOnlyMemory<byte> payload, ProjectOptions project) =>
        MapItems(payload, project, "Metric", MapMetric);

    private static IEnumerable<JsonObject> MapItems(ReadOnlyMemory<byte> payload, ProjectOptions project, string what,
        Func<JsonObject, ProjectOptions, JsonObject?> map)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(payload.Span);
        }
        catch (JsonException ex)
        {
            throw new Envelopes.EnvelopeFormatException($"{what} item payload is not valid JSON.", ex);
        }

        if (root?["items"] is not JsonArray items)
            yield break;

        foreach (var node in items)
        {
            if (node is JsonObject entry && map(entry, project) is { } record)
                yield return record;
        }
    }

    public static JsonObject Map(JsonObject entry, ProjectOptions project)
    {
        var record = NewRecord(entry, project, AsString(entry["body"]) ?? "");
        record["level"] = NormalizeLevel(AsString(entry["level"]));
        if (entry["severity_number"] is JsonValue sev && sev.TryGetValue<int>(out var sevNum))
            record["severity_number"] = sevNum;

        CopyAttributes(entry, record);
        return record;
    }

    /// <summary>
    /// One trace-connected metric value: <c>{"name", "type": "counter|gauge|distribution", "value", "unit"?, ...}</c>.
    /// Returns null for entries without a name or a numeric value.
    /// </summary>
    public static JsonObject? MapMetric(JsonObject entry, ProjectOptions project)
    {
        var name = AsString(entry["name"]);
        if (string.IsNullOrEmpty(name) || entry["value"] is not JsonValue value ||
            value.GetValueKind() != JsonValueKind.Number)
            return null;

        var type = AsString(entry["type"]) ?? "counter";
        var unit = AsString(entry["unit"]);
        var number = value.ToJsonString();

        var record = NewRecord(entry, project, unit is { Length: > 0 } ? $"{name} {number} {unit}" : $"{name} {number}");
        record["kind"] = "metric";
        record["metric.name"] = name;
        record["metric.type"] = type;
        record["metric.value"] = value.DeepClone();
        if (unit is { Length: > 0 })
            record["metric.unit"] = unit;

        CopyAttributes(entry, record);
        return record;
    }

    private static JsonObject NewRecord(JsonObject entry, ProjectOptions project, string message)
    {
        var record = new JsonObject
        {
            ["_time"] = FormatTimestamp(entry["timestamp"]),
            ["_msg"] = message,
            ["service"] = project.Service,
            ["project"] = project.Id,
            ["source"] = "sentry-sdk",
        };

        if (project.Environment is not null)
            record["env"] = project.Environment;
        if (AsString(entry["trace_id"]) is { Length: > 0 } traceId)
            record["trace_id"] = traceId;
        if (AsString(entry["span_id"]) is { Length: > 0 } spanId)
            record["span_id"] = spanId;
        return record;
    }

    private static void CopyAttributes(JsonObject entry, JsonObject record)
    {
        if (entry["attributes"] is not JsonObject attributes)
            return;

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
