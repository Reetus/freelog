using FreeLog.Relay;
using FreeLog.Relay.Forwarding;
using FreeLog.Relay.Sinks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Projects/keys usually live in a mounted file (see deploy/compose/relay.example.json).
if (Environment.GetEnvironmentVariable("FREELOG_CONFIG_FILE") is { Length: > 0 } configFile)
    builder.Configuration.AddJsonFile(configFile, optional: false, reloadOnChange: false);

builder.Logging.AddJsonConsole(o =>
{
    o.UseUtcTimestamp = true;
    o.TimestampFormat = "O";
});

builder.Services.AddOptions<RelayOptions>()
    .Bind(builder.Configuration.GetSection(RelayOptions.Section))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<RelayOptions>, RelayOptionsValidator>();

builder.Services.AddRequestDecompression(); // gzip / br / deflate per Content-Encoding (Sentry SDKs gzip)
builder.Services.AddSingleton<RelayMetrics>();
builder.Services.AddSingleton<LogQueue>();
builder.Services.AddHttpClient<ILogSink, VictoriaLogsSink>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHostedService<LogWriterService>();
builder.Services.AddHttpClient<IForwardTransport, HttpForwardTransport>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<EnvelopeForwarder>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EnvelopeForwarder>());

var app = builder.Build();

app.UseRequestDecompression();

// Trailing slash matters to Sentry SDKs (they post to .../envelope/); routing accepts both.
app.MapPost("/api/{projectId}/envelope", EnvelopeEndpoint.HandleAsync);
app.MapGet("/healthz", () => Results.Ok("ok"));
app.MapGet("/metrics", (RelayMetrics m, LogQueue q) => Results.Text(m.Render(q.Count), "text/plain; version=0.0.4"));

app.Run();

public partial class Program;
