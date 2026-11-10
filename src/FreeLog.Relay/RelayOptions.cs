using System.Security.Cryptography;
using System.Text;

namespace FreeLog.Relay;

public sealed class RelayOptions
{
    public const string Section = "FreeLog";

    public VictoriaLogsOptions VictoriaLogs { get; set; } = new();
    public QueueOptions Queue { get; set; } = new();
    public ForwardingOptions Forwarding { get; set; } = new();
    public List<ProjectOptions> Projects { get; set; } = [];

    /// <summary>Max accepted (decompressed) envelope size in bytes.</summary>
    public long MaxEnvelopeBytes { get; set; } = 20 * 1024 * 1024;

    public ProjectOptions? FindProject(string projectId, string? publicKey)
    {
        if (string.IsNullOrEmpty(publicKey))
            return null;

        var keyBytes = Encoding.UTF8.GetBytes(publicKey);
        foreach (var p in Projects)
        {
            if (p.Id == projectId &&
                CryptographicOperations.FixedTimeEquals(keyBytes, Encoding.UTF8.GetBytes(p.Key)))
                return p;
        }

        return null;
    }

    public IEnumerable<string> Validate()
    {
        if (!Uri.TryCreate(VictoriaLogs.Url, UriKind.Absolute, out _))
            yield return "FreeLog:VictoriaLogs:Url must be an absolute URL.";
        if (Projects.Count == 0)
            yield return "FreeLog:Projects must contain at least one project.";

        for (var i = 0; i < Projects.Count; i++)
        {
            var p = Projects[i];
            if (string.IsNullOrWhiteSpace(p.Id))
                yield return $"FreeLog:Projects:{i}:Id is required.";
            if (string.IsNullOrWhiteSpace(p.Key) || p.Key.Length < 16)
                yield return $"FreeLog:Projects:{i}:Key is required and must be at least 16 characters.";
            else if (p.Key.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase))
                yield return $"FreeLog:Projects:{i}:Key is still the example placeholder; generate one with `openssl rand -hex 16`.";
            if (string.IsNullOrWhiteSpace(p.Service))
                yield return $"FreeLog:Projects:{i}:Service is required.";
            if (p.VictoriaLogsUrl is not null && !Uri.TryCreate(p.VictoriaLogsUrl, UriKind.Absolute, out _))
                yield return $"FreeLog:Projects:{i}:VictoriaLogsUrl must be an absolute URL.";
            if (!string.IsNullOrWhiteSpace(p.ForwardDsn) && !FreeLog.Relay.Forwarding.SentryDsn.TryParse(p.ForwardDsn, out _))
                yield return $"FreeLog:Projects:{i}:ForwardDsn must look like http(s)://<key>@<host>/<projectId>.";
        }

        foreach (var dup in Projects.GroupBy(p => (p.Id, p.Key)).Where(g => g.Count() > 1))
            yield return $"FreeLog:Projects has duplicate Id/Key for project '{dup.Key.Id}'.";
    }
}

public sealed class VictoriaLogsOptions
{
    public string Url { get; set; } = "http://localhost:9428";

    /// <summary>Low-cardinality fields that identify a log stream in VictoriaLogs.</summary>
    public string StreamFields { get; set; } = "service,env";

    /// <summary>Optional VictoriaLogs tenant headers.</summary>
    public string? AccountId { get; set; }
    public string? ProjectId { get; set; }
}

public sealed class QueueOptions
{
    public int Capacity { get; set; } = 100_000;
    public int MaxBatchSize { get; set; } = 1_000;
    public int FlushIntervalMs { get; set; } = 1_000;
    public int MaxRetries { get; set; } = 5;
}

public sealed class ProjectOptions
{
    /// <summary>The numeric/opaque id in the DSN path: https://key@host/{Id}.</summary>
    public string Id { get; set; } = "";

    /// <summary>The DSN public key. Treat it as a write-only API key.</summary>
    public string Key { get; set; } = "";

    /// <summary>Default <c>service</c> field. A <c>service.name</c> attribute on a log overrides it.</summary>
    public string Service { get; set; } = "";

    /// <summary>Default <c>env</c> field when the SDK doesn't send <c>sentry.environment</c>.</summary>
    public string? Environment { get; set; }

    /// <summary>Send this project's logs to a different VictoriaLogs instance (e.g. a longer-retention tier).</summary>
    public string? VictoriaLogsUrl { get; set; }

    /// <summary>
    /// Optional Sentry-compatible DSN (e.g. GlitchTip) that receives this project's non-log items:
    /// errors, transactions, user feedback. Logs always go to VictoriaLogs only.
    /// </summary>
    public string? ForwardDsn { get; set; }

    private Forwarding.SentryDsn? _forward;
    private bool _forwardParsed;

    public Forwarding.SentryDsn? GetForwardTarget()
    {
        if (!_forwardParsed)
        {
            Forwarding.SentryDsn.TryParse(ForwardDsn, out _forward);
            _forwardParsed = true;
        }

        return _forward;
    }
}

public sealed class ForwardingOptions
{
    /// <summary>Envelope item types sent to a project's ForwardDsn. Everything else is dropped.</summary>
    public List<string> ItemTypes { get; set; } = ["event", "transaction", "attachment", "user_report", "feedback"];

    public int QueueCapacity { get; set; } = 10_000;
    public int MaxRetries { get; set; } = 3;
}

public sealed class RelayOptionsValidator : Microsoft.Extensions.Options.IValidateOptions<RelayOptions>
{
    public Microsoft.Extensions.Options.ValidateOptionsResult Validate(string? name, RelayOptions options)
    {
        var problems = options.Validate().ToList();
        return problems.Count == 0
            ? Microsoft.Extensions.Options.ValidateOptionsResult.Success
            : Microsoft.Extensions.Options.ValidateOptionsResult.Fail(problems);
    }
}
