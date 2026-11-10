using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace FreeLog.Relay.Sinks;

/// <summary>Writes gzip-compressed JSON lines to VictoriaLogs' <c>/insert/jsonline</c> endpoint.</summary>
public sealed class VictoriaLogsSink(HttpClient http, IOptions<RelayOptions> options) : ILogSink
{
    public async Task WriteAsync(string target, IReadOnlyList<JsonObject> records, CancellationToken ct)
    {
        var vl = options.Value.VictoriaLogs;
        var url = $"{target.TrimEnd('/')}/insert/jsonline" +
                  $"?_stream_fields={Uri.EscapeDataString(vl.StreamFields)}&_time_field=_time&_msg_field=_msg";

        using var body = new MemoryStream();
        await using (var gzip = new GZipStream(body, CompressionLevel.Fastest, leaveOpen: true))
        await using (var writer = new Utf8JsonWriter(gzip))
        {
            foreach (var record in records)
            {
                record.WriteTo(writer);
                await writer.FlushAsync(ct);
                writer.Reset();
                gzip.WriteByte((byte)'\n');
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new ByteArrayContent(body.GetBuffer(), 0, (int)body.Length);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/stream+json");
        request.Content.Headers.ContentEncoding.Add("gzip");
        if (!string.IsNullOrEmpty(vl.AccountId))
            request.Headers.Add("AccountID", vl.AccountId);
        if (!string.IsNullOrEmpty(vl.ProjectId))
            request.Headers.Add("ProjectID", vl.ProjectId);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"VictoriaLogs returned {(int)response.StatusCode}: {text[..Math.Min(text.Length, 500)]}",
                null, response.StatusCode);
        }
    }
}
