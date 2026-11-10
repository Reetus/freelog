using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace FreeLog.Relay.Forwarding;

public sealed record ForwardRequest(SentryDsn Target, byte[] Envelope, string? ClientIp);

/// <summary>Sends one envelope; throws on failure. Abstracted for tests.</summary>
public interface IForwardTransport
{
    Task<HttpStatusCode> SendAsync(ForwardRequest request, CancellationToken ct);
}

public sealed class HttpForwardTransport(HttpClient http) : IForwardTransport
{
    public async Task<HttpStatusCode> SendAsync(ForwardRequest request, CancellationToken ct)
    {
        using var body = new MemoryStream();
        await using (var gzip = new GZipStream(body, CompressionLevel.Fastest, leaveOpen: true))
            await gzip.WriteAsync(request.Envelope, ct);

        using var message = new HttpRequestMessage(HttpMethod.Post, request.Target.EnvelopeUri);
        message.Content = new ByteArrayContent(body.GetBuffer(), 0, (int)body.Length);
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-sentry-envelope");
        message.Content.Headers.ContentEncoding.Add("gzip");
        message.Headers.TryAddWithoutValidation("X-Sentry-Auth",
            $"Sentry sentry_version=7, sentry_client=freelog-relay/1.0, sentry_key={request.Target.PublicKey}");
        if (request.ClientIp is not null)
            message.Headers.TryAddWithoutValidation("X-Forwarded-For", request.ClientIp);

        using var response = await http.SendAsync(message, ct);
        return response.StatusCode;
    }
}

/// <summary>
/// Forwards non-log envelope items (errors, transactions, …) to a Sentry-compatible server such as GlitchTip.
/// Runs in the background so SDKs get an immediate response; bounded queue, a few retries, then drop (counted).
/// </summary>
public sealed class EnvelopeForwarder(
    IForwardTransport transport,
    RelayMetrics metrics,
    IOptions<RelayOptions> options,
    ILogger<EnvelopeForwarder> logger) : BackgroundService
{
    private readonly Channel<ForwardRequest> _queue = Channel.CreateBounded<ForwardRequest>(
        new BoundedChannelOptions(options.Value.Forwarding.QueueCapacity) { SingleReader = true });

    public bool TryEnqueue(ForwardRequest request)
    {
        if (_queue.Writer.TryWrite(request))
            return true;
        metrics.Inc(ref metrics.ForwardDropped);
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
                await SendWithRetryAsync(request, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task SendWithRetryAsync(ForwardRequest request, CancellationToken ct)
    {
        var maxRetries = options.Value.Forwarding.MaxRetries;
        for (var attempt = 0; ; attempt++)
        {
            string failure;
            try
            {
                var status = await transport.SendAsync(request, ct);
                var code = (int)status;
                if (code is >= 200 and < 300)
                {
                    metrics.Inc(ref metrics.ForwardSucceeded);
                    return;
                }

                // 4xx other than 429 won't succeed on retry (bad key, project deleted, payload rejected).
                if (code is >= 400 and < 500 && code != 429)
                {
                    metrics.Inc(ref metrics.ForwardRejected);
                    logger.LogWarning("Forward to {Target} rejected with {Status}; dropping envelope",
                        request.Target.EnvelopeUri, code);
                    return;
                }

                failure = $"HTTP {code}";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }

            metrics.Inc(ref metrics.ForwardErrors);
            if (attempt >= maxRetries)
            {
                metrics.Inc(ref metrics.ForwardDropped);
                logger.LogError("Dropping envelope for {Target} after {Attempts} attempts: {Error}",
                    request.Target.EnvelopeUri, attempt + 1, failure);
                return;
            }

            var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
            logger.LogWarning("Forward to {Target} failed (attempt {Attempt}): {Error}. Retrying in {Delay}",
                request.Target.EnvelopeUri, attempt + 1, failure, delay);
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
