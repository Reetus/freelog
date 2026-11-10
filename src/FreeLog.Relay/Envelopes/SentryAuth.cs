using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace FreeLog.Relay.Envelopes;

/// <summary>Extracts the public key (<c>sentry_key</c>) a Sentry SDK authenticates with.</summary>
public static class SentryAuth
{
    /// <summary>
    /// Looks in, in order: the <c>X-Sentry-Auth</c> header, the <c>Authorization: Sentry ...</c> header,
    /// the <c>sentry_key</c> query parameter, and the <c>dsn</c> field of the envelope header.
    /// </summary>
    public static string? GetPublicKey(HttpRequest request, JsonObject? envelopeHeader = null)
    {
        foreach (var headerName in new[] { "X-Sentry-Auth", "Authorization" })
        {
            var key = ParseAuthHeader(request.Headers[headerName].ToString());
            if (key is not null)
                return key;
        }

        var query = request.Query["sentry_key"].ToString();
        if (!string.IsNullOrEmpty(query))
            return query;

        if (envelopeHeader?["dsn"] is JsonValue dsnNode && dsnNode.TryGetValue<string>(out var dsn) &&
            Uri.TryCreate(dsn, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo))
        {
            return Uri.UnescapeDataString(uri.UserInfo.Split(':')[0]);
        }

        return null;
    }

    /// <summary>Parses <c>Sentry sentry_version=7, sentry_client=x, sentry_key=abc</c>.</summary>
    public static string? ParseAuthHeader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var span = value.AsSpan().Trim();
        if (!span.StartsWith("Sentry ", StringComparison.OrdinalIgnoreCase))
            return null;

        foreach (var part in span[7..].ToString().Split(','))
        {
            var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length == 2 && kv[0].Equals("sentry_key", StringComparison.OrdinalIgnoreCase) && kv[1].Length > 0)
                return kv[1];
        }

        return null;
    }
}
