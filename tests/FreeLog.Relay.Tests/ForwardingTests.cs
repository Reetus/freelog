using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FreeLog.Relay.Envelopes;
using FreeLog.Relay.Forwarding;

namespace FreeLog.Relay.Tests;

public class SentryDsnTests
{
    [Theory]
    [InlineData("https://abc@glitchtip.example.com/1", "abc", "1", "https://glitchtip.example.com/api/1/envelope/")]
    [InlineData("http://abc:secret@glitchtip:8000/42", "abc", "42", "http://glitchtip:8000/api/42/envelope/")]
    [InlineData("https://abc@example.com/sub/path/9/", "abc", "9", "https://example.com/sub/path/api/9/envelope/")]
    public void Parses_valid_dsns(string dsn, string key, string project, string envelope)
    {
        Assert.True(SentryDsn.TryParse(dsn, out var parsed));
        Assert.Equal(key, parsed!.PublicKey);
        Assert.Equal(project, parsed.ProjectId);
        Assert.Equal(envelope, parsed.EnvelopeUri.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://glitchtip.example.com/1")]   // no key
    [InlineData("https://abc@glitchtip.example.com/")] // no project
    [InlineData("ftp://abc@host/1")]
    public void Rejects_invalid_dsns(string? dsn) => Assert.False(SentryDsn.TryParse(dsn, out _));
}

public class EnvelopeWriterTests
{
    [Fact]
    public void Round_trips_items_and_replaces_dsn()
    {
        var binary = "a\nb";
        var raw = "{\"event_id\":\"e1\",\"dsn\":\"http://relaykey@relay/3\"}\n" +
                  "{\"type\":\"event\"}\n{\"message\":\"hi\"}\n" +
                  $"{{\"type\":\"attachment\",\"length\":{binary.Length}}}\n{binary}\n" +
                  "{\"type\":\"log\"}\n{\"items\":[]}\n";
        var env = EnvelopeParser.Parse(Encoding.UTF8.GetBytes(raw));

        var written = EnvelopeWriter.Write(env.Header, env.Items.Where(i => i.Type != "log"), "http://gt@glitchtip/7");
        var back = EnvelopeParser.Parse(written);

        Assert.Equal("e1", back.Header["event_id"]!.ToString());
        Assert.Equal("http://gt@glitchtip/7", back.Header["dsn"]!.ToString());
        Assert.Equal(["event", "attachment"], back.Items.Select(i => i.Type));
        Assert.Equal("{\"message\":\"hi\"}", Encoding.UTF8.GetString(back.Items[0].Payload.Span));
        Assert.Equal(binary, Encoding.UTF8.GetString(back.Items[1].Payload.Span));
    }

    [Fact]
    public void Leaves_header_without_dsn_alone()
    {
        var written = EnvelopeWriter.Write(new JsonObject { ["event_id"] = "x" }, [], "http://gt@h/1");
        Assert.Equal("{\"event_id\":\"x\"}\n", Encoding.UTF8.GetString(written));
    }
}

public class ForwardingEndpointTests : IDisposable
{
    private readonly TestRelay _relay = new();

    public void Dispose() => _relay.Dispose();

    private const string MixedEnvelope = """
        {"event_id":"9ec79c33ec9942ab8353589fcb2e04dc"}
        {"type":"event"}
        {"event_id":"9ec79c33ec9942ab8353589fcb2e04dc","level":"error","exception":{"values":[{"type":"InvalidOperationException","value":"boom"}]}}
        {"type":"log","item_count":1,"content_type":"application/vnd.sentry.items.log+json"}
        {"items":[{"timestamp":1731200000.5,"trace_id":"0af7651916cd43dd8448eb211c80319c","level":"error","body":"boom"}]}
        {"type":"client_report"}
        {"discarded_events":[]}
        {"type":"session"}
        {"sid":"s1"}

        """;

    private static HttpRequestMessage Post(string projectId, string key)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/{projectId}/envelope/")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(MixedEnvelope)),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-sentry-envelope");
        req.Headers.TryAddWithoutValidation("X-Sentry-Auth", $"Sentry sentry_version=7, sentry_key={key}");
        return req;
    }

    [Fact]
    public async Task Forwards_errors_to_glitchtip_and_logs_to_victorialogs()
    {
        var res = await _relay.CreateClient().SendAsync(Post("3", TestRelay.ForwardingKey));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        await _relay.Forward.WaitForCallsAsync(1);
        var sent = Assert.Single(_relay.Forward.Sent);
        Assert.Equal("http://glitchtip:8000/api/7/envelope/", sent.Target.EnvelopeUri.ToString());
        Assert.Equal("gtkey123", sent.Target.PublicKey);

        var forwarded = EnvelopeParser.Parse(sent.Envelope);
        Assert.Equal(["event"], forwarded.Items.Select(i => i.Type)); // not log, client_report or session
        Assert.Equal("9ec79c33ec9942ab8353589fcb2e04dc", forwarded.Header["event_id"]!.ToString());

        var logs = await _relay.Sink.WaitForAsync(1);
        Assert.Equal("billing", Assert.Single(logs)["service"]!.ToString());

        var metrics = await _relay.CreateClient().GetStringAsync("/metrics");
        Assert.Contains("freelog_forward_envelopes_total{outcome=\"ok\"} 1", metrics);
        Assert.Contains("freelog_forward_items_total 1", metrics);
        Assert.Contains("freelog_items_ignored_total{type=\"session\"} 1", metrics);
    }

    [Fact]
    public async Task Projects_without_forward_dsn_drop_errors()
    {
        await _relay.CreateClient().SendAsync(Post("1", TestRelay.Key));
        await _relay.Sink.WaitForAsync(1);
        await Task.Delay(100);

        Assert.Equal(0, _relay.Forward.Calls);
        var metrics = await _relay.CreateClient().GetStringAsync("/metrics");
        Assert.Contains("freelog_items_ignored_total{type=\"event\"} 1", metrics);
    }

    [Fact]
    public async Task Retries_server_errors_but_not_client_errors()
    {
        _relay.Forward.Responses.Enqueue(HttpStatusCode.ServiceUnavailable);
        await _relay.CreateClient().SendAsync(Post("3", TestRelay.ForwardingKey));
        await _relay.Forward.WaitForCallsAsync(2, TimeSpan.FromSeconds(15));
        Assert.Single(_relay.Forward.Sent);

        _relay.Forward.Responses.Enqueue(HttpStatusCode.Unauthorized);
        await _relay.CreateClient().SendAsync(Post("3", TestRelay.ForwardingKey));
        await _relay.Forward.WaitForCallsAsync(3);
        await Task.Delay(1500); // a retry would have happened after 1s
        Assert.Equal(3, _relay.Forward.Calls);

        var metrics = await _relay.CreateClient().GetStringAsync("/metrics");
        Assert.Contains("freelog_forward_envelopes_total{outcome=\"rejected\"} 1", metrics);
        Assert.Contains("freelog_forward_errors_total 1", metrics);
    }
}
