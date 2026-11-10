using System.Text;
using System.Text.Json.Nodes;
using FreeLog.Relay.Envelopes;
using FreeLog.Relay.Logs;
using Microsoft.AspNetCore.Http;

namespace FreeLog.Relay.Tests;

public class LogRecordMapperTests
{
    private static readonly ProjectOptions Project = new() { Id = "1", Key = "k", Service = "orders-api", Environment = "staging" };

    [Fact]
    public void Maps_core_fields_and_unwraps_attributes()
    {
        var entry = JsonNode.Parse("""
            {
              "timestamp": 1731200000.125,
              "trace_id": "0af7651916cd43dd8448eb211c80319c",
              "span_id": "b7ad6b7169203331",
              "level": "warn",
              "body": "Retry 3 of 5",
              "severity_number": 13,
              "attributes": {
                "retry.count": { "value": 3, "type": "integer" },
                "sentry.environment": { "value": "production", "type": "string" },
                "sentry.release": { "value": "1.2.3", "type": "string" },
                "sentry.message.template": { "value": "Retry {Attempt} of {Max}", "type": "string" },
                "level": { "value": "spoofed", "type": "string" },
                "plain": "raw-value"
              }
            }
            """)!.AsObject();

        var r = LogRecordMapper.Map(entry, Project);

        Assert.Equal("2024-11-10T00:53:20.1250000Z", r["_time"]!.ToString());
        Assert.Equal("Retry 3 of 5", r["_msg"]!.ToString());
        Assert.Equal("warn", r["level"]!.ToString());
        Assert.Equal(13, r["severity_number"]!.GetValue<int>());
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", r["trace_id"]!.ToString());
        Assert.Equal("b7ad6b7169203331", r["span_id"]!.ToString());
        Assert.Equal("orders-api", r["service"]!.ToString());
        Assert.Equal("production", r["env"]!.ToString()); // SDK environment beats project default
        Assert.Equal("1.2.3", r["release"]!.ToString());
        Assert.Equal("Retry {Attempt} of {Max}", r["message_template"]!.ToString());
        Assert.Equal(3, r["retry.count"]!.GetValue<int>());
        Assert.Equal("spoofed", r["attr.level"]!.ToString());
        Assert.Equal("raw-value", r["plain"]!.ToString());
        Assert.Equal("sentry-sdk", r["source"]!.ToString());
    }

    [Fact]
    public void Falls_back_to_project_defaults_and_now()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var r = LogRecordMapper.Map(new JsonObject { ["body"] = "hi" }, Project);

        Assert.Equal("staging", r["env"]!.ToString());
        Assert.Equal("info", r["level"]!.ToString());
        Assert.True(DateTimeOffset.Parse(r["_time"]!.ToString()) >= before);
        Assert.Null(r["trace_id"]);
    }

    [Fact]
    public void Service_name_attribute_overrides_project_service()
    {
        var entry = new JsonObject
        {
            ["body"] = "x",
            ["attributes"] = new JsonObject { ["service.name"] = new JsonObject { ["value"] = "worker", ["type"] = "string" } },
        };
        Assert.Equal("worker", LogRecordMapper.Map(entry, Project)["service"]!.ToString());
    }

    [Theory]
    [InlineData("Information", "info")]
    [InlineData("WARNING", "warn")]
    [InlineData("Critical", "fatal")]
    [InlineData("err", "error")]
    [InlineData("Verbose", "trace")]
    [InlineData(null, "info")]
    [InlineData("custom", "custom")]
    public void Normalizes_levels(string? input, string expected) =>
        Assert.Equal(expected, LogRecordMapper.NormalizeLevel(input));

    [Fact]
    public void Ignores_non_object_items_and_missing_items_array()
    {
        Assert.Empty(LogRecordMapper.MapLogItem(Encoding.UTF8.GetBytes("{}"), Project));
        Assert.Single(LogRecordMapper.MapLogItem(Encoding.UTF8.GetBytes("""{"items":[1,{"body":"a"},null]}"""), Project));
    }
}

public class SentryAuthTests
{
    [Theory]
    [InlineData("Sentry sentry_version=7, sentry_client=sentry.dotnet/6.12.1, sentry_key=abc123", "abc123")]
    [InlineData("sentry sentry_key=abc123,sentry_version=7", "abc123")]
    [InlineData("Bearer abc", null)]
    [InlineData("Sentry sentry_version=7", null)]
    [InlineData("", null)]
    public void Parses_auth_header(string header, string? expected) =>
        Assert.Equal(expected, SentryAuth.ParseAuthHeader(header));

    [Fact]
    public void Falls_back_to_query_then_envelope_dsn()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString("?sentry_key=fromquery");
        Assert.Equal("fromquery", SentryAuth.GetPublicKey(ctx.Request));

        var header = new JsonObject { ["dsn"] = "https://fromdsn@relay.example/1" };
        Assert.Equal("fromdsn", SentryAuth.GetPublicKey(new DefaultHttpContext().Request, header));
    }
}
