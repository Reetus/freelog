using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using FreeLog.Relay.Forwarding;
using FreeLog.Relay.Sinks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FreeLog.Relay.Tests;

/// <summary>Captures what the relay would write to VictoriaLogs.</summary>
public sealed class FakeSink : ILogSink
{
    public ConcurrentQueue<(string Target, JsonObject Record)> Written { get; } = new();
    public int FailuresBeforeSuccess { get; set; }
    public int Calls;

    public Task WriteAsync(string target, IReadOnlyList<JsonObject> records, CancellationToken ct)
    {
        if (Interlocked.Increment(ref Calls) <= FailuresBeforeSuccess)
            throw new HttpRequestException("boom");
        foreach (var r in records)
            Written.Enqueue((target, r));
        return Task.CompletedTask;
    }

    public async Task<List<JsonObject>> WaitForAsync(int count, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (Written.Count < count && DateTime.UtcNow < until)
            await Task.Delay(20);
        return Written.Select(w => w.Record).ToList();
    }
}

/// <summary>Captures what the relay forwards to GlitchTip; returns queued status codes, then 200.</summary>
public sealed class FakeForwardTransport : IForwardTransport
{
    public ConcurrentQueue<ForwardRequest> Sent { get; } = new();
    public ConcurrentQueue<HttpStatusCode> Responses { get; } = new();
    public int Calls;

    public Task<HttpStatusCode> SendAsync(ForwardRequest request, CancellationToken ct)
    {
        var status = Responses.TryDequeue(out var s) ? s : HttpStatusCode.OK;
        if ((int)status is >= 200 and < 300)
            Sent.Enqueue(request);
        Interlocked.Increment(ref Calls);
        return Task.FromResult(status);
    }

    public async Task WaitForCallsAsync(int calls, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (Calls < calls && DateTime.UtcNow < until)
            await Task.Delay(20);
    }
}

public sealed class TestRelay : WebApplicationFactory<Program>
{
    public const string Key = "testkey0000000000000000000000000";
    public const string OtherKey = "otherkey000000000000000000000000";
    public const string ForwardingKey = "fwdkey00000000000000000000000000";
    public const string GlitchTipDsn = "http://gtkey123@glitchtip:8000/7";
    public FakeSink Sink { get; } = new();
    public FakeForwardTransport Forward { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("FreeLog:VictoriaLogs:Url", "http://vl-default:9428");
        builder.UseSetting("FreeLog:Queue:FlushIntervalMs", "20");
        builder.UseSetting("FreeLog:Queue:MaxRetries", "2");
        builder.UseSetting("FreeLog:Projects:0:Id", "1");
        builder.UseSetting("FreeLog:Projects:0:Key", Key);
        builder.UseSetting("FreeLog:Projects:0:Service", "orders-api");
        builder.UseSetting("FreeLog:Projects:1:Id", "2");
        builder.UseSetting("FreeLog:Projects:1:Key", OtherKey);
        builder.UseSetting("FreeLog:Projects:1:Service", "audit");
        builder.UseSetting("FreeLog:Projects:1:Environment", "prod");
        builder.UseSetting("FreeLog:Projects:1:VictoriaLogsUrl", "http://vl-longterm:9428");

        builder.UseSetting("FreeLog:Projects:2:Id", "3");
        builder.UseSetting("FreeLog:Projects:2:Key", ForwardingKey);
        builder.UseSetting("FreeLog:Projects:2:Service", "billing");
        builder.UseSetting("FreeLog:Projects:2:ForwardDsn", GlitchTipDsn);
        builder.UseSetting("FreeLog:Forwarding:MaxRetries", "2");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ILogSink>();
            services.AddSingleton<ILogSink>(Sink);
            services.RemoveAll<IForwardTransport>();
            services.AddSingleton<IForwardTransport>(Forward);
        });
    }
}

/// <summary>Tests that initialize the global <c>SentrySdk</c> must not run in parallel with each other.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SentrySdkCollection
{
    public const string Name = "SentrySdk (global state)";
}
