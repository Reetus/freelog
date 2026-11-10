namespace FreeLog.Relay.Forwarding;

/// <summary>A parsed Sentry/GlitchTip DSN: <c>{scheme}://{publicKey}@{host}[:port][/path]/{projectId}</c>.</summary>
public sealed record SentryDsn(string Raw, string PublicKey, string ProjectId, Uri EnvelopeUri)
{
    public static bool TryParse(string? dsn, out SentryDsn? result)
    {
        result = null;
        if (!Uri.TryCreate(dsn, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return false;

        var key = Uri.UnescapeDataString(uri.UserInfo.Split(':')[0]);
        var path = uri.AbsolutePath.TrimEnd('/');
        var lastSlash = path.LastIndexOf('/');
        var projectId = lastSlash >= 0 ? path[(lastSlash + 1)..] : "";
        if (key.Length == 0 || projectId.Length == 0)
            return false;

        var prefix = path[..lastSlash];
        var envelope = new UriBuilder(uri.Scheme, uri.Host, uri.Port, $"{prefix}/api/{projectId}/envelope/").Uri;
        result = new SentryDsn(dsn!, key, projectId, envelope);
        return true;
    }
}
