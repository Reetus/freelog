using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FreeLog.Logging;
using FreeLog.Relay.Logs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sentry;
using Sentry.Extensions.Logging;
using Xunit.Abstractions;

namespace FreeLog.Relay.Tests;

public class MetricMapperTests
{
    private static readonly ProjectOptions Project = new() { Id = "1", Key = "k", Service = "orders-api", Environment = "prod" };

    [Fact]
    public void Maps_metric_fields_and_attributes()
    {
        var entry = JsonNode.Parse("""
            {
              "timestamp": 1746456149.019,
              "trace_id": "624f66e93a04469f9992c7e9f1485056",
              "span_id": "b0e6f15b45c36b12",
              "name": "api.response_time",
              "type": "distribution",
              "value": 125.5,
              "unit": "millisecond",
              "attributes": {
                "endpoint": { "value": "/api/users", "type": "string" },
                "status_code": { "value": 201, "type": "integer" },
                "kind": { "value": "spoofed", "type": "string" },
                "sentry.environment": { "value": "staging", "type": "string" }
              }
            }
            """)!.AsObject();

        var r = LogRecordMapper.MapMetric(entry, Project)!;

        Assert.Equal("metric", r["kind"]!.ToString());
        Assert.Equal("api.response_time", r["metric.name"]!.ToString());
        Assert.Equal("distribution", r["metric.type"]!.ToString());
        Assert.Equal(125.5, r["metric.value"]!.GetValue<double>());
        Assert.Equal("millisecond", r["metric.unit"]!.ToString());
        Assert.Equal("api.response_time 125.5 millisecond", r["_msg"]!.ToString());
        Assert.Equal("2025-05-05T14:42:29.0190000Z", r["_time"]!.ToString());
        Assert.Equal("/api/users", r["endpoint"]!.ToString());
        Assert.Equal(201, r["status_code"]!.GetValue<int>());
        Assert.Equal("spoofed", r["attr.kind"]!.ToString());
        Assert.Equal("staging", r["env"]!.ToString());
        Assert.Equal("orders-api", r["service"]!.ToString());
        Assert.Equal("624f66e93a04469f9992c7e9f1485056", r["trace_id"]!.ToString());
        Assert.Null(r["level"]);
    }

    [Theory]
    [InlineData("""{"type":"counter","value":1}""")]                // no name
    [InlineData("""{"name":"x","type":"counter"}""")]               // no value
    [InlineData("""{"name":"x","type":"counter","value":"1"}""")]   // non-numeric value
    public void Skips_invalid_metrics(string json) =>
        Assert.Null(LogRecordMapper.MapMetric(JsonNode.Parse(json)!.AsObject(), Project));

    [Fact]
    public void Defaults_type_and_omits_missing_unit()
    {
        var r = LogRecordMapper.MapMetric(JsonNode.Parse("""{"name":"orders.placed","value":3}""")!.AsObject(), Project)!;
        Assert.Equal("counter", r["metric.type"]!.ToString());
        Assert.Null(r["metric.unit"]);
        Assert.Equal("orders.placed 3", r["_msg"]!.ToString());
    }
}

public class MetricEndpointTests : IDisposable
{
    private readonly TestRelay _relay = new();

    public void Dispose() => _relay.Dispose();

    [Fact]
    public async Task Stores_trace_metric_items_next_to_logs()
    {
        const string envelope = """
            {}
            {"type":"trace_metric","item_count":2,"content_type":"application/vnd.sentry.items.trace-metric+json"}
            {"version":2,"items":[{"timestamp":1746456149.0,"trace_id":"624f66e93a04469f9992c7e9f1485056","name":"orders.placed","type":"counter","value":1},{"timestamp":1746456149.0,"trace_id":"624f66e93a04469f9992c7e9f1485056","name":"queue.depth","type":"gauge","value":17}]}
            {"type":"log","item_count":1,"content_type":"application/vnd.sentry.items.log+json"}
            {"items":[{"timestamp":1746456149.0,"trace_id":"624f66e93a04469f9992c7e9f1485056","level":"info","body":"hello"}]}

            """;
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/1/envelope/") { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(envelope)) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-sentry-envelope");
        req.Headers.TryAddWithoutValidation("X-Sentry-Auth", $"Sentry sentry_version=7, sentry_key={TestRelay.Key}");

        var res = await _relay.CreateClient().SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var records = await _relay.Sink.WaitForAsync(3);
        Assert.Equal(3, records.Count);
        Assert.Equal(["orders.placed", "queue.depth"],
            records.Where(r => r["kind"]?.ToString() == "metric").Select(r => r["metric.name"]!.ToString()).Order());
        Assert.Single(records, r => r["_msg"]!.ToString() == "hello");

        var metrics = await _relay.CreateClient().GetStringAsync("/metrics");
        Assert.Contains("freelog_metrics_received_total 2", metrics);
        Assert.Contains("freelog_logs_received_total 1", metrics);
    }
}

/// <summary>Real Sentry SDK metrics API → relay → stored records.</summary>
[Collection(SentrySdkCollection.Name)]
public class SentrySdkMetricsEndToEndTests(ITestOutputHelper output) : IDisposable
{
    private readonly TestRelay _relay = new();

    public void Dispose() => _relay.Dispose();

    [Fact]
    public async Task SentrySdk_Metrics_counters_gauges_and_distributions_are_stored()
    {
        var captured = new List<string>();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Staging" });
        builder.Configuration["FreeLog:Dsn"] = $"http://{TestRelay.Key}@localhost/1";
        builder.Configuration["FreeLog:Service"] = "checkout";
        builder.AddFreeLog();
        builder.Services.Configure<SentryLoggingOptions>(o =>
            o.CreateHttpMessageHandler = () => new CapturingHandler(_relay.Server.CreateHandler(), captured));

        using var meter = new Meter("FreeLog.Tests.Builtin");
        var builtin = meter.CreateCounter<long>("builtin.requests");

        using (var host = builder.Build())
        {
            await host.StartAsync();

            SentrySdk.Metrics.EmitCounter("orders.placed", 1,
                [new KeyValuePair<string, object>("region", "eu"), new("plan", "pro")], null);
            SentrySdk.Metrics.EmitCounter("orders.placed", 2,
                [new KeyValuePair<string, object>("region", "us")], null);
            SentrySdk.Metrics.EmitGauge("queue.depth", 17);
            SentrySdk.Metrics.EmitDistribution("checkout.duration", 125.5, "millisecond");

            // Does the SDK forward System.Diagnostics.Metrics instruments on its own? (it shouldn't, by default)
            for (var i = 0; i < 5; i++)
                builtin.Add(1);

            await host.StopAsync();
        }

        await _relay.Sink.WaitForAsync(4);
        await Task.Delay(300);
        foreach (var c in captured)
            output.WriteLine(c);

        var metrics = _relay.Sink.Written.Select(w => w.Record).Where(r => r["kind"]?.ToString() == "metric").ToList();
        foreach (var m in metrics)
            output.WriteLine(m.ToJsonString());

        Assert.DoesNotContain(metrics, m => m["metric.name"]!.ToString().StartsWith("builtin."));
        Assert.Equal(4, metrics.Count);

        var eu = metrics.Single(m => m["metric.name"]!.ToString() == "orders.placed" && m["region"]?.ToString() == "eu");
        Assert.Equal("counter", eu["metric.type"]!.ToString());
        Assert.Equal(1, eu["metric.value"]!.GetValue<double>());
        Assert.Equal("pro", eu["plan"]!.ToString());
        Assert.Equal("checkout", eu["service"]!.ToString());     // FreeLog:Service applied to metrics too
        Assert.Equal("Staging", eu["env"]!.ToString());
        Assert.Matches("^[0-9a-f]{32}$", eu["trace_id"]!.ToString());

        var gauge = metrics.Single(m => m["metric.name"]!.ToString() == "queue.depth");
        Assert.Equal("gauge", gauge["metric.type"]!.ToString());
        Assert.Equal(17, gauge["metric.value"]!.GetValue<double>());

        var dist = metrics.Single(m => m["metric.name"]!.ToString() == "checkout.duration");
        Assert.Equal("distribution", dist["metric.type"]!.ToString());
        Assert.Equal(125.5, dist["metric.value"]!.GetValue<double>());
        Assert.Equal("millisecond", dist["metric.unit"]!.ToString());
    }

    private sealed class CapturingHandler(HttpMessageHandler inner, List<string> captured) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
            var gz = request.Content.Headers.ContentEncoding.Contains("gzip");
            var text = gz
                ? new StreamReader(new System.IO.Compression.GZipStream(new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress)).ReadToEnd()
                : Encoding.UTF8.GetString(bytes);
            lock (captured)
                captured.Add(text);

            var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Content = new ByteArrayContent(bytes) };
            foreach (var h in request.Headers)
                copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
            foreach (var h in request.Content.Headers)
                copy.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            return await base.SendAsync(copy, ct);
        }
    }
}
