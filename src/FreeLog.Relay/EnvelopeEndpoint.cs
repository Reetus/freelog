using FreeLog.Relay.Envelopes;
using FreeLog.Relay.Forwarding;
using FreeLog.Relay.Logs;
using FreeLog.Relay.Sinks;
using Microsoft.Extensions.Options;

namespace FreeLog.Relay;

/// <summary>
/// <c>POST /api/{projectId}/envelope/</c> — the endpoint a Sentry SDK sends to when its DSN is
/// <c>https://{key}@{relay-host}/{projectId}</c>. Log items are queued for VictoriaLogs. Errors, transactions
/// and feedback are forwarded to the project's <c>ForwardDsn</c> (e.g. GlitchTip) when set; everything else
/// (sessions, client reports, …) is acknowledged and dropped.
/// </summary>
public static class EnvelopeEndpoint
{
    private const string LogItemType = "log";

    public static async Task<IResult> HandleAsync(
        string projectId,
        HttpRequest request,
        IOptions<RelayOptions> options,
        LogQueue queue,
        EnvelopeForwarder forwarder,
        RelayMetrics metrics,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var opts = options.Value;
        var logger = loggerFactory.CreateLogger("FreeLog.Relay.Envelope");

        byte[] body;
        try
        {
            body = await ReadBodyAsync(request, opts.MaxEnvelopeBytes, ct);
        }
        catch (InvalidDataException ex)
        {
            metrics.Inc(ref metrics.EnvelopesInvalid);
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        Envelope envelope;
        try
        {
            envelope = EnvelopeParser.Parse(body);
        }
        catch (EnvelopeFormatException ex)
        {
            metrics.Inc(ref metrics.EnvelopesInvalid);
            logger.LogDebug(ex, "Rejected malformed envelope for project {ProjectId}", projectId);
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        var project = opts.FindProject(projectId, SentryAuth.GetPublicKey(request, envelope.Header));
        if (project is null)
        {
            metrics.Inc(ref metrics.EnvelopesUnauthorized);
            return Results.Problem("Unknown project id or sentry_key.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var target = project.VictoriaLogsUrl ?? opts.VictoriaLogs.Url;
        var forwardTarget = project.GetForwardTarget();
        List<EnvelopeItem>? toForward = null;

        foreach (var item in envelope.Items)
        {
            if (item.Type != LogItemType)
            {
                if (forwardTarget is not null && opts.Forwarding.ItemTypes.Contains(item.Type))
                    (toForward ??= []).Add(item);
                else
                    metrics.IgnoredItem(item.Type);
                continue;
            }

            try
            {
                foreach (var record in LogRecordMapper.MapLogItem(item.Payload, project))
                {
                    metrics.Inc(ref metrics.LogsReceived);
                    queue.TryEnqueue(new PendingRecord(target, record));
                }
            }
            catch (EnvelopeFormatException ex)
            {
                // One bad item shouldn't fail the rest of the envelope.
                metrics.Inc(ref metrics.EnvelopesInvalid);
                logger.LogDebug(ex, "Skipped malformed log item for project {ProjectId}", projectId);
            }
        }

        if (toForward is not null)
        {
            metrics.Add(ref metrics.ItemsForwarded, toForward.Count);
            var bytes = EnvelopeWriter.Write(envelope.Header, toForward, forwardTarget!.Raw);
            forwarder.TryEnqueue(new ForwardRequest(forwardTarget, bytes,
                request.HttpContext.Connection.RemoteIpAddress?.ToString()));
        }

        metrics.Inc(ref metrics.EnvelopesAccepted);
        var eventId = envelope.Header["event_id"]?.ToString();
        return Results.Json(new { id = eventId });
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, long maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new InvalidDataException($"Envelope exceeds {maxBytes} bytes.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
