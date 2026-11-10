using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace FreeLog.Relay.Tests;

public class EndpointTests : IDisposable
{
    private readonly TestRelay _relay = new();

    public void Dispose() => _relay.Dispose();

    private const string LogEnvelope = """
        {"event_id":"9ec79c33ec9942ab8353589fcb2e04dc","sent_at":"2026-11-10T10:00:00Z"}
        {"type":"log","item_count":2,"content_type":"application/vnd.sentry.items.log+json"}
        {"items":[{"timestamp":1731200000.5,"trace_id":"0af7651916cd43dd8448eb211c80319c","level":"info","body":"first"},{"timestamp":1731200001.5,"trace_id":"0af7651916cd43dd8448eb211c80319c","level":"error","body":"second"}]}
        {"type":"event"}
        {"message":"ignored"}
        {"type":"client_report"}
        {"discarded_events":[]}

        """;

    private static HttpRequestMessage Post(string path, string body, string? key = TestRelay.Key, bool gzip = false)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        if (gzip)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest))
                gz.Write(bytes);
            bytes = ms.ToArray();
        }

        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(bytes) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-sentry-envelope");
        if (gzip)
            req.Content.Headers.ContentEncoding.Add("gzip");
        if (key is not null)
            req.Headers.TryAddWithoutValidation("X-Sentry-Auth", $"Sentry sentry_version=7, sentry_key={key}");
        return req;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accepts_logs_plain_and_gzip_with_trailing_slash(bool gzip)
    {
        var client = _relay.CreateClient();
        var res = await client.SendAsync(Post("/api/1/envelope/", LogEnvelope, gzip: gzip));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("9ec79c33ec9942ab8353589fcb2e04dc", await res.Content.ReadAsStringAsync());

        var written = await _relay.Sink.WaitForAsync(2);
        Assert.Equal(["first", "second"], written.Select(r => r["_msg"]!.ToString()));
        Assert.All(_relay.Sink.Written, w => Assert.Equal("http://vl-default:9428", w.Target));
        Assert.Equal("orders-api", written[0]["service"]!.ToString());
    }

    [Fact]
    public async Task Routes_project_to_its_own_victorialogs_instance()
    {
        var client = _relay.CreateClient();
        var res = await client.SendAsync(Post("/api/2/envelope/", LogEnvelope, key: TestRelay.OtherKey));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        await _relay.Sink.WaitForAsync(2);
        Assert.All(_relay.Sink.Written, w =>
        {
            Assert.Equal("http://vl-longterm:9428", w.Target);
            Assert.Equal("audit", w.Record["service"]!.ToString());
            Assert.Equal("prod", w.Record["env"]!.ToString());
        });
    }

    [Theory]
    [InlineData("/api/1/envelope/", null)]
    [InlineData("/api/1/envelope/", "wrongkey00000000000000000000000")]
    [InlineData("/api/2/envelope/", TestRelay.Key)] // valid key, wrong project
    public async Task Rejects_bad_credentials(string path, string? key)
    {
        var res = await _relay.CreateClient().SendAsync(Post(path, LogEnvelope, key));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Empty(_relay.Sink.Written);
    }

    [Fact]
    public async Task Accepts_key_in_query_string()
    {
        var res = await _relay.CreateClient().SendAsync(Post($"/api/1/envelope/?sentry_key={TestRelay.Key}", LogEnvelope, key: null));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Rejects_malformed_envelope()
    {
        var res = await _relay.CreateClient().SendAsync(Post("/api/1/envelope/", "garbage"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Metrics_reflect_traffic()
    {
        var client = _relay.CreateClient();
        await client.SendAsync(Post("/api/1/envelope/", LogEnvelope));
        await client.SendAsync(Post("/api/1/envelope/", LogEnvelope, key: null));
        await _relay.Sink.WaitForAsync(2);

        var metrics = await client.GetStringAsync("/metrics");
        Assert.Contains("freelog_envelopes_total{outcome=\"accepted\"} 1", metrics);
        Assert.Contains("freelog_envelopes_total{outcome=\"unauthorized\"} 1", metrics);
        Assert.Contains("freelog_logs_received_total 2", metrics);
        Assert.Contains("freelog_items_ignored_total{type=\"event\"} 1", metrics);
        Assert.Contains("freelog_items_ignored_total{type=\"client_report\"} 1", metrics);
    }

    [Fact]
    public async Task Retries_failed_sink_writes()
    {
        _relay.Sink.FailuresBeforeSuccess = 1;
        await _relay.CreateClient().SendAsync(Post("/api/1/envelope/", LogEnvelope));

        var written = await _relay.Sink.WaitForAsync(2, TimeSpan.FromSeconds(15));
        Assert.Equal(2, written.Count);
        Assert.Equal(2, _relay.Sink.Calls);
    }
}
