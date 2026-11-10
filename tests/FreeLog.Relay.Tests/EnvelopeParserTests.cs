using System.Text;
using FreeLog.Relay.Envelopes;

namespace FreeLog.Relay.Tests;

public class EnvelopeParserTests
{
    private static Envelope Parse(string s) => EnvelopeParser.Parse(Encoding.UTF8.GetBytes(s));

    [Fact]
    public void Parses_items_without_length()
    {
        var env = Parse("{\"event_id\":\"abc\"}\n{\"type\":\"log\"}\n{\"items\":[]}\n{\"type\":\"event\"}\n{\"x\":1}\n");

        Assert.Equal("abc", env.Header["event_id"]!.ToString());
        Assert.Equal(["log", "event"], env.Items.Select(i => i.Type));
        Assert.Equal("{\"items\":[]}", Encoding.UTF8.GetString(env.Items[0].Payload.Span));
    }

    [Fact]
    public void Honours_explicit_length_even_when_payload_contains_newlines()
    {
        var payload = "line1\nline2";
        var env = Parse($"{{}}\n{{\"type\":\"attachment\",\"length\":{payload.Length}}}\n{payload}\n{{\"type\":\"log\"}}\n{{\"items\":[]}}");

        Assert.Equal(2, env.Items.Count);
        Assert.Equal(payload, Encoding.UTF8.GetString(env.Items[0].Payload.Span));
        Assert.Equal("log", env.Items[1].Type);
    }

    [Fact]
    public void Handles_crlf_trailing_blank_lines_and_missing_final_newline()
    {
        var env = Parse("{}\r\n{\"type\":\"log\"}\r\n{\"items\":[]}\r\n\r\n");
        Assert.Single(env.Items);
        Assert.Equal("{\"items\":[]}", Encoding.UTF8.GetString(env.Items[0].Payload.Span));

        env = Parse("{}\n{\"type\":\"log\"}\n{\"items\":[]}");
        Assert.Single(env.Items);
    }

    [Fact]
    public void Zero_length_item_is_allowed()
    {
        var env = Parse("{}\n{\"type\":\"attachment\",\"length\":0}\n\n{\"type\":\"log\"}\n{\"items\":[]}\n");
        Assert.Equal(2, env.Items.Count);
        Assert.Equal(0, env.Items[0].Payload.Length);
    }

    [Theory]
    [InlineData("not json\n")]
    [InlineData("{}\n{\"no_type\":1}\n{}\n")]
    [InlineData("{}\n{\"type\":\"log\",\"length\":999}\n{}\n")]
    [InlineData("[]\n")]
    public void Rejects_malformed_envelopes(string raw) =>
        Assert.Throws<EnvelopeFormatException>(() => Parse(raw));
}
