using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.DependencyInjection;

namespace FreeLog.Logging;

public sealed class FreeLogOptions
{
    /// <summary>
    /// freelog-relay DSN: <c>http(s)://{key}@{relay-host}/{projectId}</c>.
    /// When empty, freelog is disabled and the app logs to the console only.
    /// </summary>
    public string? Dsn { get; set; }

    /// <summary>Overrides the relay's per-project service name (sent as <c>service.name</c>).</summary>
    public string? Service { get; set; }

    /// <summary>Defaults to the host environment name (Development/Staging/Production).</summary>
    public string? Environment { get; set; }

    public string? Release { get; set; }

    /// <summary>Minimum level shipped to freelog.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    /// <summary>
    /// Minimum level still written to the console when freelog is enabled, so
    /// <c>docker logs</c> keeps showing problems even if the relay is unreachable.
    /// </summary>
    public LogLevel ConsoleMinimumLevel { get; set; } = LogLevel.Warning;

    /// <summary>Directory where the SDK persists envelopes while the relay is down. Null = memory only.</summary>
    public string? CacheDirectoryPath { get; set; }

    /// <summary>
    /// Also send <c>LogError</c>/<c>LogCritical</c> calls as Sentry error events (with Information+ breadcrumbs),
    /// which the relay forwards to the project's <c>ForwardDsn</c> (GlitchTip) for grouping and alerts.
    /// Logs still go to VictoriaLogs either way. Unhandled exceptions are always sent as events.
    /// </summary>
    public bool CaptureErrors { get; set; }

    /// <summary>Print Sentry SDK diagnostics (useful when logs aren't arriving).</summary>
    public bool Debug { get; set; }
}

public static class FreeLogExtensions
{
    public const string ConfigSection = "FreeLog";

    /// <summary>
    /// Ships <see cref="ILogger"/> output (including exceptions and scopes) to freelog-relay via the
    /// Sentry SDK's structured logs.
    /// Reads the <c>FreeLog</c> configuration section (env vars: <c>FreeLog__Dsn</c>, …).
    /// Works for any <see cref="IHostApplicationBuilder"/>: WebApplicationBuilder, HostApplicationBuilder, workers.
    /// </summary>
    /// <remarks>
    /// Don't combine with <c>UseSentry()</c>/<c>SentrySdk.Init()</c> — that would initialize the SDK twice.
    /// Apps already using Sentry should just set <c>EnableLogs = true</c> and point their DSN at the relay.
    /// </remarks>
    public static TBuilder AddFreeLog<TBuilder>(this TBuilder builder, Action<FreeLogOptions>? configure = null)
        where TBuilder : IHostApplicationBuilder
    {
        var options = new FreeLogOptions();
        builder.Configuration.GetSection(ConfigSection).Bind(options);
        configure?.Invoke(options);

        if (string.IsNullOrWhiteSpace(options.Dsn))
            return builder; // disabled: console logging stays as configured

        var environment = options.Environment ?? builder.Environment.EnvironmentName;

        builder.Logging.AddSentry(o =>
        {
            o.Dsn = options.Dsn;
            o.Environment = environment;
            o.Release = options.Release;
            o.CacheDirectoryPath = options.CacheDirectoryPath;
            o.Debug = options.Debug;
            o.SendDefaultPii = false;

            // The SDK's own ILogger → logs integration drops exceptions and scopes; FreeLogLoggerProvider
            // replaces it. SentrySdk.Logger ships regardless of this flag (SDK >= 6.10).
            o.EnableLogs = false;

            // Error events are only useful with a ForwardDsn (GlitchTip) on the relay; otherwise it drops them.
            o.MinimumEventLevel = options.CaptureErrors ? LogLevel.Error : LogLevel.None;
            o.MinimumBreadcrumbLevel = options.CaptureErrors ? LogLevel.Information : LogLevel.None;
            o.SetBeforeSend(ErrorFingerprint.Apply);

            if (!string.IsNullOrWhiteSpace(options.Service))
            {
                var service = options.Service;
                o.SetBeforeSendLog(log =>
                {
                    log.SetAttribute("service.name", service);
                    return log;
                });
                o.SetBeforeSendMetric(metric =>
                {
                    metric.SetAttribute("service.name", service);
                    return metric;
                });
            }
        });

        // MinimumLevel is a floor applied inside the provider, so the app's normal
        // Logging:LogLevel category rules (e.g. Microsoft.AspNetCore=Warning) still apply on top.
        builder.Logging.AddFreeLogProvider(options.MinimumLevel);
        builder.Logging.AddFilter<ConsoleLoggerProvider>(null, options.ConsoleMinimumLevel);
        return builder;
    }

    /// <summary>
    /// Adds only the freelog <see cref="ILogger"/> → Sentry structured-log bridge, for apps that already
    /// initialize Sentry themselves (<c>UseSentry()</c> / <c>SentrySdk.Init()</c>). Point that DSN at the relay
    /// and leave the SDK's own <c>EnableLogs</c> off to avoid duplicates.
    /// </summary>
    public static ILoggingBuilder AddFreeLogProvider(this ILoggingBuilder logging, LogLevel minimumLevel = LogLevel.Information)
    {
        logging.Services.AddSingleton<ILoggerProvider>(new FreeLogLoggerProvider(minimumLevel));
        return logging;
    }
}
