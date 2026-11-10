using System.Collections.Concurrent;
using System.Text;

namespace FreeLog.Relay;

/// <summary>Minimal counters exposed in Prometheus text format at <c>/metrics</c>.</summary>
public sealed class RelayMetrics
{
    public long EnvelopesAccepted;
    public long EnvelopesUnauthorized;
    public long EnvelopesInvalid;
    public long LogsReceived;
    public long LogsWritten;
    public long LogsDroppedQueueFull;
    public long LogsDroppedSinkFailure;
    public long SinkErrors;
    public long ForwardSucceeded;
    public long ForwardRejected;
    public long ForwardErrors;
    public long ForwardDropped;
    public long ItemsForwarded;

    private readonly ConcurrentDictionary<string, long> _ignoredItems = new();

    public void Inc(ref long counter) => Interlocked.Increment(ref counter);
    public void Add(ref long counter, long value) => Interlocked.Add(ref counter, value);

    public void IgnoredItem(string type) =>
        _ignoredItems.AddOrUpdate(Sanitize(type), 1, (_, v) => v + 1);

    public string Render(int queueDepth)
    {
        var sb = new StringBuilder();
        Counter(sb, "freelog_envelopes_total", "Envelopes received by outcome.",
            ("outcome=\"accepted\"", EnvelopesAccepted),
            ("outcome=\"unauthorized\"", EnvelopesUnauthorized),
            ("outcome=\"invalid\"", EnvelopesInvalid));
        Counter(sb, "freelog_logs_received_total", "Log records parsed from envelopes.", ("", LogsReceived));
        Counter(sb, "freelog_logs_written_total", "Log records written to the sink.", ("", LogsWritten));
        Counter(sb, "freelog_logs_dropped_total", "Log records dropped.",
            ("reason=\"queue_full\"", LogsDroppedQueueFull),
            ("reason=\"sink_failure\"", LogsDroppedSinkFailure));
        Counter(sb, "freelog_sink_errors_total", "Failed sink write attempts (including retried).", ("", SinkErrors));
        Counter(sb, "freelog_items_ignored_total", "Envelope items neither stored nor forwarded.",
            _ignoredItems.Select(kv => ($"type=\"{kv.Key}\"", kv.Value)).ToArray());

        Counter(sb, "freelog_forward_envelopes_total", "Envelopes forwarded to a ForwardDsn, by outcome.",
            ("outcome=\"ok\"", ForwardSucceeded),
            ("outcome=\"rejected\"", ForwardRejected),
            ("outcome=\"dropped\"", ForwardDropped));
        Counter(sb, "freelog_forward_errors_total", "Failed forward attempts (including retried).", ("", ForwardErrors));
        Counter(sb, "freelog_forward_items_total", "Envelope items queued for forwarding.", ("", ItemsForwarded));

        sb.Append("# HELP freelog_queue_depth Log records waiting to be written.\n");
        sb.Append("# TYPE freelog_queue_depth gauge\n");
        sb.Append("freelog_queue_depth ").Append(queueDepth).Append('\n');
        return sb.ToString();
    }

    private static void Counter(StringBuilder sb, string name, string help, params (string Labels, long Value)[] series)
    {
        sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(" counter\n");
        foreach (var (labels, value) in series)
        {
            sb.Append(name);
            if (labels.Length > 0)
                sb.Append('{').Append(labels).Append('}');
            sb.Append(' ').Append(value).Append('\n');
        }
    }

    private static string Sanitize(string s) =>
        new(s.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').Take(40).ToArray());
}
