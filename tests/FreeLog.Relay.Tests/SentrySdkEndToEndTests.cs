using System.Text;
using FreeLog.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sentry.Extensions.Logging;
using Xunit.Abstractions;

namespace FreeLog.Relay.Tests;

/// <summary>
/// Uses the real Sentry .NET SDK (via <see cref="FreeLogExtensions.AddFreeLog{TBuilder}"/>) against the
/// in-memory relay, so the test covers the SDK's actual wire format, compression and auth header.
/// </summary>
[Collection(SentrySdkCollection.Name)]
public class SentrySdkEndToEndTests(ITestOutputHelper output) : IDisposable
{
    private readonly TestRelay _relay = new();

    public void Dispose() => _relay.Dispose();

    [Fact]
    public async Task ILogger_calls_arrive_as_structured_records()
    {
        var captured = new List<string>();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Staging" });
        builder.Configuration["FreeLog:Dsn"] = $"http://{TestRelay.Key}@localhost/1";
        builder.Configuration["FreeLog:Release"] = "orders@1.2.3";
        builder.AddFreeLog();
        builder.Services.Configure<SentryLoggingOptions>(o =>
            o.CreateHttpMessageHandler = () => new CapturingHandler(_relay.Server.CreateHandler(), captured));

        using (var host = builder.Build())
        {
            await host.StartAsync();
            var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Orders.Checkout");

            logger.LogDebug("below minimum level, not shipped");
            using (logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = "req-1" }))
                logger.LogInformation("Order {OrderId} placed by {Customer}", 42, "alice");
            logger.LogWarning("Literal {braces} without args stay intact");
            try
            {
                throw new InvalidOperationException("payment declined");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Checkout failed for order {OrderId}", 43);
            }

            await host.StopAsync();
        } // disposing the host flushes the SDK

        await _relay.Sink.WaitForAsync(3);
        await Task.Delay(200);
        foreach (var body in captured)
            output.WriteLine(body);
        var records = _relay.Sink.Written.Select(w => w.Record)
            .Where(r => r["category.name"]?.ToString() == "Orders.Checkout")
            .ToList();
        foreach (var r in records)
            output.WriteLine(r.ToJsonString());

        Assert.Equal(3, records.Count); // Debug is below the default Information floor

        var info = records.Single(r => r["level"]!.ToString() == "info");
        Assert.Equal("Order 42 placed by alice", info["_msg"]!.ToString());
        Assert.Equal("orders-api", info["service"]!.ToString());
        Assert.Equal("Staging", info["env"]!.ToString());
        Assert.Equal("orders@1.2.3", info["release"]!.ToString());
        Assert.Equal("Order {OrderId} placed by {Customer}", info["message_template"]!.ToString());
        Assert.Matches("^[0-9a-f]{32}$", info["trace_id"]!.ToString());
        Assert.Equal(42, info["OrderId"]!.GetValue<int>());
        Assert.Equal("alice", info["Customer"]!.ToString());
        Assert.Equal("req-1", info["RequestId"]!.ToString());

        var warn = records.Single(r => r["level"]!.ToString() == "warn");
        Assert.Equal("Literal {braces} without args stay intact", warn["_msg"]!.ToString());

        var error = records.Single(r => r["level"]!.ToString() == "error");
        Assert.Equal("Checkout failed for order 43", error["_msg"]!.ToString());
        Assert.Equal("System.InvalidOperationException", error["exception.type"]!.ToString());
        Assert.Equal("payment declined", error["exception.message"]!.ToString());
        Assert.Contains("SentrySdkEndToEndTests", error["exception.stacktrace"]!.ToString());
        Assert.Null(error["RequestId"]); // scope ended
    }

    /// <summary>
    /// Apps that use the plain Sentry SDK (no FreeLog.Logging) with <c>EnableLogs = true</c> are supported too:
    /// both the SDK's own ILogger integration and the direct <c>SentrySdk.Logger</c> API.
    /// </summary>
    [Fact]
    public async Task Plain_Sentry_sdk_with_EnableLogs_is_supported()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Production" });
        builder.Logging.AddSentry(o =>
        {
            o.Dsn = $"http://{TestRelay.Key}@localhost/1";
            o.EnableLogs = true;
            o.CreateHttpMessageHandler = () => _relay.Server.CreateHandler();
        });

        using (var host = builder.Build())
        {
            await host.StartAsync();
            var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Plain.Sdk");
            logger.LogWarning("Stock low for {Sku}", "ABC-1");
            SentrySdk.Logger.LogInfo("Direct API order {0}", 42);
            await host.StopAsync();
        }

        await _relay.Sink.WaitForAsync(2);
        await Task.Delay(200);
        var records = _relay.Sink.Written.Select(w => w.Record).ToList();

        var viaILogger = records.Single(r => r["_msg"]?.ToString() == "Stock low for ABC-1");
        Assert.Equal("warn", viaILogger["level"]!.ToString());
        Assert.Equal("orders-api", viaILogger["service"]!.ToString());
        Assert.Equal("production", viaILogger["env"]!.ToString()); // SDK default when Environment isn't set
        Assert.Equal("Stock low for {Sku}", viaILogger["message_template"]!.ToString());
        Assert.Equal("ABC-1", viaILogger["sentry.message.parameter.Sku"]!.ToString()); // SDK's naming, not "Sku"

        var direct = records.Single(r => r["_msg"]?.ToString() == "Direct API order 42");
        Assert.Equal("info", direct["level"]!.ToString());
    }

    [Fact]
    public async Task CaptureErrors_sends_error_events_that_get_forwarded()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Staging" });
        builder.Configuration["FreeLog:Dsn"] = $"http://{TestRelay.ForwardingKey}@localhost/3";
        builder.Configuration["FreeLog:CaptureErrors"] = "true";
        builder.AddFreeLog();
        builder.Services.Configure<SentryLoggingOptions>(o =>
            o.CreateHttpMessageHandler = () => _relay.Server.CreateHandler());

        using (var host = builder.Build())
        {
            await host.StartAsync();
            var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Billing");
            logger.LogInformation("Charging card"); // becomes a breadcrumb on the event
            foreach (var invoice in new[] { 7, 8 })
            {
                try
                {
                    throw new InvalidOperationException($"card declined for invoice {invoice}");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Charge failed for invoice {InvoiceId}", invoice);
                }
            }

            await host.StopAsync();
        }

        await _relay.Forward.WaitForCallsAsync(2);
        var events = _relay.Forward.Sent
            .SelectMany(r => FreeLog.Relay.Envelopes.EnvelopeParser.Parse(r.Envelope).Items)
            .Where(i => i.Type == "event")
            .Select(i => System.Text.Json.Nodes.JsonNode.Parse(i.Payload.Span)!)
            .ToList();
        foreach (var e in events)
            output.WriteLine(e.ToJsonString());

        Assert.Equal(2, events.Count);
        var evt = events[0];
        Assert.Equal("System.InvalidOperationException", evt["exception"]!["values"]![0]!["type"]!.ToString());
        Assert.Equal("card declined for invoice 7", evt["exception"]!["values"]![0]!["value"]!.ToString());
        Assert.Contains("Charging card", evt["breadcrumbs"]!.ToJsonString());

        // Different invoice ids, same bug → same fingerprint → one GlitchTip issue.
        Assert.Equal(
            """["freelog","System.InvalidOperationException","Billing","Charge failed for invoice {InvoiceId}"]""",
            evt["fingerprint"]!.ToJsonString());
        Assert.Equal(evt["fingerprint"]!.ToJsonString(), events[1]["fingerprint"]!.ToJsonString());

        // The error is still stored as a log too.
        var logs = await _relay.Sink.WaitForAsync(2);
        Assert.Contains(logs, r => r["_msg"]!.ToString() == "Charge failed for invoice 7");
    }

    private sealed class CapturingHandler(HttpMessageHandler inner, List<string> captured) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
            var encoding = string.Join(",", request.Content.Headers.ContentEncoding);
            var text = encoding.Contains("gzip")
                ? new StreamReader(new System.IO.Compression.GZipStream(new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress)).ReadToEnd()
                : Encoding.UTF8.GetString(bytes);
            lock (captured)
                captured.Add($"POST {request.RequestUri} enc={encoding} auth={request.Headers.GetValues("X-Sentry-Auth").FirstOrDefault()}\n{text}");

            var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Content = new ByteArrayContent(bytes) };
            foreach (var h in request.Headers)
                copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
            foreach (var h in request.Content.Headers)
                copy.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            return await base.SendAsync(copy, ct);
        }
    }
}
