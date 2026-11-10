# FreeLog.Logging

Ships `ILogger` output to [freelog-relay](https://github.com/Reetus/freelog) using the Sentry .NET SDK's structured logs.
The relay stores the logs in VictoriaLogs and can optionally forward errors to GlitchTip.

```csharp
using FreeLog.Logging;

var builder = WebApplication.CreateBuilder(args);   // or Host.CreateApplicationBuilder(args)
builder.AddFreeLog();                                // no-op when FreeLog:Dsn is empty
```

```json
"FreeLog": {
  "Dsn": "http://<key>@logs.internal:5080/<projectId>",
  "Service": "orders-api",
  "MinimumLevel": "Information",
  "ConsoleMinimumLevel": "Warning",
  "CaptureErrors": false
}
```

Each record includes the rendered message, `message_template`, template parameters and `BeginScope` values as fields,
`category.name`, `exception.type`, `exception.message`, `exception.stacktrace`, `trace_id`, `env` and `release`.

Apps that already call `UseSentry()`/`SentrySdk.Init()` should use `builder.Logging.AddFreeLogProvider()` instead.

Full documentation: https://github.com/Reetus/freelog
