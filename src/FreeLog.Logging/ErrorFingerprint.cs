using Sentry;

namespace FreeLog.Logging;

/// <summary>
/// Stable grouping key for error events. GlitchTip groups by <c>title + culprit</c>, and the title contains the
/// exception message, so <c>"Card declined for order 1017"</c> and <c>"... 1018"</c> would become separate issues.
/// Grouping by exception type + logger + message template (or top in-app frame) keeps one issue per bug.
/// </summary>
public static class ErrorFingerprint
{
    public static SentryEvent Apply(SentryEvent evt)
    {
        if (evt.Fingerprint.Count > 0)
            return evt; // set explicitly by the app

        var fingerprint = For(evt);
        if (fingerprint is not null)
            evt.Fingerprint = fingerprint;
        return evt;
    }

    public static string[]? For(SentryEvent evt)
    {
        var exception = evt.SentryExceptions?.LastOrDefault();
        var type = exception?.Type ?? evt.Exception?.GetType().FullName;

        // ILogger.LogError(ex, "Checkout failed for order {OrderId}", id): the template is stable.
        var template = evt.Message?.Message;
        if (!string.IsNullOrEmpty(template))
            return ["freelog", type ?? "", evt.Logger ?? "", template];

        // Unhandled exception / CaptureException: type + where it was thrown.
        var frame = exception?.Stacktrace?.Frames.LastOrDefault(f => f.InApp == true)
                    ?? exception?.Stacktrace?.Frames.LastOrDefault();
        if (type is not null && frame is not null)
            return ["freelog", type, $"{frame.Module}.{frame.Function}"];

        return null; // fall back to the server's default grouping
    }
}
