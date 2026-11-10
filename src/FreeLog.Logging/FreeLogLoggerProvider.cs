using Microsoft.Extensions.Logging;
using Sentry;

namespace FreeLog.Logging;

/// <summary>
/// Forwards <see cref="ILogger"/> calls to the Sentry SDK's structured logger (<see cref="SentrySdk.Logger"/>),
/// which batches and ships them to freelog-relay.
/// </summary>
/// <remarks>
/// Used instead of the SDK's built-in <c>EnableLogs</c> ILogger integration because that one drops the
/// exception and ignores <see cref="ILogger.BeginScope{TState}"/>. Here exceptions become
/// <c>exception.type</c>/<c>exception.message</c>/<c>exception.stacktrace</c> (OpenTelemetry names), and
/// scope values plus template parameters become plain named attributes.
/// </remarks>
[ProviderAlias("FreeLog")]
public sealed class FreeLogLoggerProvider(LogLevel minimumLevel) : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public ILogger CreateLogger(string categoryName) => new FreeLogLogger(categoryName, this, minimumLevel);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class FreeLogLogger(string category, FreeLogLoggerProvider provider, LogLevel minimumLevel) : ILogger
    {
        // The SDK reports its own diagnostics through ILogger; shipping those would loop.
        private readonly bool _isSentryInternal = category.StartsWith("Sentry", StringComparison.Ordinal);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= minimumLevel && !_isSentryInternal && SentrySdk.IsEnabled;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message))
                message = exception?.Message ?? "";

            void Configure(SentryLog log)
            {
                log.SetAttribute("category.name", category);
                if (eventId.Id != 0)
                    log.SetAttribute("event.id", eventId.Id);
                if (!string.IsNullOrEmpty(eventId.Name))
                    log.SetAttribute("event.name", eventId.Name);

                provider._scopes.ForEachScope(static (scope, l) => AddProperties(l, scope, "scope"), log);
                AddProperties(log, state, null);

                if (exception is not null)
                {
                    log.SetAttribute("exception.type", exception.GetType().FullName ?? exception.GetType().Name);
                    log.SetAttribute("exception.message", exception.Message);
                    log.SetAttribute("exception.stacktrace", exception.ToString());
                }
            }

            // No template parameters are passed, so the SDK uses the message verbatim (no string.Format).
            var logger = SentrySdk.Logger;
            switch (logLevel)
            {
                case LogLevel.Trace: logger.LogTrace(Configure, message); break;
                case LogLevel.Debug: logger.LogDebug(Configure, message); break;
                case LogLevel.Information: logger.LogInfo(Configure, message); break;
                case LogLevel.Warning: logger.LogWarning(Configure, message); break;
                case LogLevel.Error: logger.LogError(Configure, message); break;
                case LogLevel.Critical: logger.LogFatal(Configure, message); break;
            }
        }

        private static void AddProperties(SentryLog log, object? state, string? unstructuredKey)
        {
            switch (state)
            {
                case IEnumerable<KeyValuePair<string, object?>> pairs:
                    foreach (var (key, value) in pairs)
                    {
                        if (key == "{OriginalFormat}")
                        {
                            if (unstructuredKey is null && value is string template)
                                log.SetAttribute("sentry.message.template", template);
                            continue;
                        }

                        if (value is not null)
                            log.SetAttribute(key, value);
                    }

                    break;
                case null:
                    break;
                default:
                    if (unstructuredKey is not null)
                        log.SetAttribute(unstructuredKey, state.ToString() ?? "");
                    break;
            }
        }
    }
}
