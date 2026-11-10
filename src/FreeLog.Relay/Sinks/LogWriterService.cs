using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace FreeLog.Relay.Sinks;

/// <summary>
/// Drains the queue in batches (by size or time), groups by target and writes to the sink
/// with exponential-backoff retries. On shutdown it flushes what is left once.
/// </summary>
public sealed class LogWriterService(
    LogQueue queue,
    ILogSink sink,
    RelayMetrics metrics,
    IOptions<RelayOptions> options,
    ILogger<LogWriterService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var q = options.Value.Queue;
        var batch = new List<PendingRecord>(q.MaxBatchSize);

        try
        {
            while (await queue.Reader.WaitToReadAsync(stoppingToken))
            {
                var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * q.FlushIntervalMs / 1000;
                while (batch.Count < q.MaxBatchSize)
                {
                    if (queue.Reader.TryRead(out var item))
                    {
                        batch.Add(item);
                        continue;
                    }

                    var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
                    if (remaining <= TimeSpan.Zero)
                        break;

                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    wait.CancelAfter(remaining);
                    try
                    {
                        if (!await queue.Reader.WaitToReadAsync(wait.Token))
                            break; // channel completed
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        break; // flush interval elapsed
                    }
                }

                await FlushAsync(batch, q.MaxRetries, stoppingToken);
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        // Shutdown: one best-effort attempt for everything still buffered.
        while (queue.Reader.TryRead(out var item))
            batch.Add(item);
        if (batch.Count > 0)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await FlushAsync(batch, maxRetries: 0, cts.Token);
        }
    }

    private async Task FlushAsync(List<PendingRecord> batch, int maxRetries, CancellationToken ct)
    {
        foreach (var group in batch.GroupBy(r => r.Target))
        {
            var records = group.Select(r => r.Record).ToList();
            if (!await WriteWithRetryAsync(group.Key, records, maxRetries, ct))
                metrics.Add(ref metrics.LogsDroppedSinkFailure, records.Count);
        }
    }

    private async Task<bool> WriteWithRetryAsync(string target, List<JsonObject> records, int maxRetries, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await sink.WriteAsync(target, records, ct);
                metrics.Add(ref metrics.LogsWritten, records.Count);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                metrics.Inc(ref metrics.SinkErrors);
                if (attempt >= maxRetries)
                {
                    logger.LogError(ex, "Dropping {Count} log records for {Target} after {Attempts} attempts",
                        records.Count, target, attempt + 1);
                    return false;
                }

                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
                logger.LogWarning("Write to {Target} failed (attempt {Attempt}): {Error}. Retrying in {Delay}",
                    target, attempt + 1, ex.Message, delay);
                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
        }
    }
}
