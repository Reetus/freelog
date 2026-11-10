# freelog

[![CI](https://github.com/Reetus/freelog/actions/workflows/ci.yml/badge.svg)](https://github.com/Reetus/freelog/actions/workflows/ci.yml)

Self-hosted, no-budget log pipeline: a free alternative to Datadog Logs for Docker, Kubernetes and C# apps.

```
 PATH 1: no code changes (any container, plain text or JSON)
 app ──stdout──▶ Docker / kubelet log files ◀── docker logs / kubectl logs (unchanged)
                          │  read-only
                          ▼
                       Vector ──────────────────────────────┐
                                                            ▼
 PATH 2: Sentry .NET SDK (FreeLog.Logging)             VictoriaLogs ──▶ built-in UI / Grafana
 app ──ILogger──▶ Sentry SDK ──▶ freelog-relay ─────────────┘   (retention, storage, LogsQL)
                                      │ errors (optional, per project)
                                      ▼
                                  GlitchTip  (issue grouping, alerts)
```

| Component | What it does |
|---|---|
| **VictoriaLogs** | Stores and indexes logs (every JSON field is searchable), enforces retention, has a web UI at `:9428/select/vmui`. |
| **Vector** | Reads container stdout/stderr from Docker (API) or Kubernetes (`/var/log/pods`), parses JSON/MEL/Serilog output, merges stack traces. |
| **freelog-relay** (`src/FreeLog.Relay`) | Sentry-compatible envelope endpoint. Writes `log` items to VictoriaLogs; forwards errors/transactions to the project's `ForwardDsn` (GlitchTip) if set, otherwise drops them. |
| **GlitchTip** (optional) | Open-source, Sentry-compatible error tracker: groups exceptions into issues, alerts on new/regressed issues. |
| **FreeLog.Logging** (`src/FreeLog.Logging`) | `builder.AddFreeLog()` for C# apps: ships `ILogger` output, with exceptions and scopes, via the Sentry SDK to the relay. |

Both paths produce the same field names (`service`, `env`, `level`, `message`, `trace_id`, `exception.*`, …), so one query covers everything. See [docs/conventions.md](docs/conventions.md).

## Quick start (single Docker host)

```sh
cd deploy/compose
cp .env.example .env                 # retention, env/host tags, ports
cp relay.example.json relay.json     # one entry per app that uses the SDK; keys: openssl rand -hex 16
docker compose up -d                 # VictoriaLogs + Vector + relay
docker compose --profile grafana up -d   # optional: Grafana on 127.0.0.1:3000 with the datasource provisioned
docker compose --profile glitchtip up -d # optional: GlitchTip on 127.0.0.1:8000 (see "Error tracking")
docker compose --profile demo up -d      # optional: demo apps for both ingestion paths
```

Open http://localhost:9428/select/vmui and try `service:demo-sdk level:error`.

**Before production**, set log rotation on every Docker host. Docker doesn't rotate by default, so logs can fill the disk:
copy [deploy/docker/daemon.json](deploy/docker/daemon.json) to `/etc/docker/daemon.json` (Docker Desktop: Settings → Docker Engine),
restart Docker, and recreate containers.

Kubernetes: see [deploy/k8s/README.md](deploy/k8s/README.md).

## Sending logs from C# apps

### Option A: nothing (stdout)
Vector already collects whatever the container prints, but **plain text is only searchable as text**:
`Order 1451 placed` has no `OrderId` field. Switch .NET apps to JSON console output to get every template
parameter and scope value as a field. **No code change needed**, just container env vars:

```yaml
environment:
  Logging__Console__FormatterName: json
  Logging__Console__FormatterOptions__IncludeScopes: "true"   # RequestId, TraceId, BeginScope values
```

(Equivalent in code: `builder.Logging.AddJsonConsole(o => o.IncludeScopes = true)`. Serilog's `CompactJsonFormatter` works too.)
The default MEL text format (`info: Category[0]` + indented message) is still parsed for level and category.
For old plain-text logs you can extract fields at query time: `"placed by" | extract "Order <OrderId> placed"`.

### Option B: Sentry SDK via FreeLog.Logging (recommended for structured logs)

```csharp
using FreeLog.Logging;

var builder = WebApplication.CreateBuilder(args);   // or Host.CreateApplicationBuilder(args)
builder.AddFreeLog();                                // no-op when FreeLog:Dsn is empty
```

```jsonc
// appsettings.json (or env vars FreeLog__Dsn, FreeLog__Service, …)
"FreeLog": {
  "Dsn": "http://<key>@logs.internal:5080/<projectId>",   // from relay.json
  "Service": "orders-api",                                 // optional, overrides relay.json
  "MinimumLevel": "Information",                           // floor for shipping; Logging:LogLevel rules still apply
  "ConsoleMinimumLevel": "Warning",                        // console keeps warnings+ so `docker logs` still shows problems
  "CacheDirectoryPath": "/tmp/freelog",                    // optional: persist envelopes while the relay is down
  "CaptureErrors": false                                   // true: errors also go to GlitchTip (see below)
}
```

Then add the label `freelog.collect=false` to that container/pod so Vector doesn't also collect its console output.

What you get per record: rendered message, `message_template`, every template parameter and `BeginScope` value as its own field,
`category.name`, `event.id`, `exception.type`, `exception.message`, `exception.stacktrace`, `trace_id`/`span_id`, `env`, `release`.

**Why a custom provider instead of the SDK's `EnableLogs`?** In Sentry 6.12 the SDK's own `ILogger` integration
drops the exception and ignores scopes. `FreeLogLoggerProvider` sends through `SentrySdk.Logger` with those added.

### Option C: plain Sentry SDK (no FreeLog package)

Any Sentry SDK (.NET, JS, Python, …) works against the relay. Point the DSN at it and enable logs:

```csharp
builder.WebHost.UseSentry(o => { o.Dsn = "http://<key>@relay-host:5080/<projectId>"; o.EnableLogs = true; });
```

Compared with Option B you lose, as of SDK 6.12:
- exceptions on log records: the SDK's ILogger integration drops them
- `BeginScope` values (`RequestId`, …)
- short field names: parameters arrive as `sentry.message.parameter.OrderId` instead of `OrderId` (still searchable)
- issue grouping in GlitchTip, unless you set fingerprints yourself

**Hybrid (apps already on Sentry that want Option B's records)**: keep your `UseSentry()`/`SentrySdk.Init()` with the DSN
pointed at the relay, leave `EnableLogs` off (otherwise every log is sent twice), and add
`builder.Logging.AddFreeLogProvider();`.

Distribute the package with `dotnet pack src/FreeLog.Logging -c Release -o artifacts` and push it to your NuGet feed,
or reference the project directly. It targets net8.0 and net10.0.

## Custom metrics from code

Similar to CloudWatch custom metrics: emit values with the Sentry SDK's metrics API (works with Option B or C,
no extra setup), and the relay stores each value as a record in VictoriaLogs (`kind=metric`). You aggregate at query time.

```csharp
SentrySdk.Metrics.EmitCounter("orders.placed", 1, [new KeyValuePair<string, object>("region", "eu")], null);
SentrySdk.Metrics.EmitGauge("queue.depth", queue.Count);
SentrySdk.Metrics.EmitDistribution("checkout.duration", elapsed.TotalMilliseconds, "millisecond");
```

Stored fields: `metric.name`, `metric.type` (`counter`/`gauge`/`distribution`), `metric.value`, `metric.unit`, the
attributes you pass (`region`), plus `service`, `env`, `release` and `trace_id`, so a metric can be joined to the logs of the
same request.

```
kind:metric metric.name:orders.placed | stats by (_time:5m, region) sum(metric.value) orders
kind:metric metric.name:checkout.duration _time:24h | stats by (_time:1h) quantile(0.95, metric.value) p95, avg(metric.value) avg
kind:metric metric.name:queue.depth | stats by (_time:1m) max(metric.value) depth
kind:metric | stats by (service, metric.name) count() values     # which metrics exist
-kind:metric service:orders-api                                    # logs only, without metric records
```

Good for business and app-level metrics, up to a few per request. Each value is one row, so it's not meant for
per-cache-hit or per-request instrumentation at high volume. For that, use `System.Diagnostics.Metrics` with an OpenTelemetry
exporter and a time-series database such as VictoriaMetrics. The SDK does **not** forward `System.Diagnostics.Metrics`
instruments on its own: only explicit `SentrySdk.Metrics` calls are sent (covered by a test).

## Error tracking with GlitchTip (optional)

Logs answer "what happened"; GlitchTip answers "what's broken": identical exceptions are grouped into one issue with
counts, first/last seen, release and breadcrumbs (the log lines leading up to the error), and it alerts on new or
regressed issues (email, Slack/Discord/webhooks).

1. In `deploy/compose/.env` set `GLITCHTIP_SECRET_KEY` (`openssl rand -hex 32`), `GLITCHTIP_DB_PASSWORD`, `GLITCHTIP_DOMAIN`
   and, for alert emails, `GLITCHTIP_EMAIL_URL`. Then `docker compose --profile glitchtip up -d`.
2. Open http://localhost:8000, register, create an organization and a project per app. Then set
   `GLITCHTIP_ENABLE_REGISTRATION=False` and recreate the container.
3. Copy the project's DSN into `relay.json` as `ForwardDsn`, with the host changed to the in-network address:
   ```json
   { "Id": "2", "Key": "…", "Service": "orders-api",
     "ForwardDsn": "http://<glitchtip-key>@glitchtip:8000/<glitchtip-project-id>" }
   ```
   Restart the relay.
4. In the app set `FreeLog:CaptureErrors=true`. `LogError`/`LogCritical` calls then also become error events,
   with `Information`+ logs as breadcrumbs. Unhandled exceptions are sent either way.

Apps don't talk to GlitchTip directly. Their DSN stays the relay's, so turning GlitchTip on or off is a relay config change.
Errors are still stored as logs in VictoriaLogs too.

**Grouping**: GlitchTip groups by exception title, which includes the message, so `Card declined for order 1017` and
`… 1018` would be separate issues. FreeLog.Logging sets a fingerprint of exception type + logger category + message
template (or the top in-app stack frame for unhandled exceptions), giving one issue per bug. Apps can still set
their own fingerprint.

Resource use: about 250 MB RAM for GlitchTip and its Postgres, with Valkey and GlitchTip's own log storage turned off.
Event retention: `GLITCHTIP_RETENTION_DAYS` (default 30 here).

## Retention

| Where | Setting |
|---|---|
| Compose | `FREELOG_RETENTION` (e.g. `14d`, `8w`, `1y`) and `FREELOG_MAX_DISK` (oldest data is dropped when exceeded) in `.env` |
| Kubernetes | `server.retentionPeriod` / `server.retentionDiskSpaceUsage` in `deploy/k8s/victorialogs-values.yaml` |
| Per app (relay) | Give the project a `VictoriaLogsUrl` pointing at a second VictoriaLogs instance with a different retention |

## Querying (LogsQL)

```
service:orders-api level:error                      errors for one service
"payment declined"                                  phrase anywhere in the message
trace_id:0af7651916cd43dd8448eb211c80319c           everything for one request
exception.type:InvalidOperationException _time:1h   last hour
service:orders-api | stats by (level) count()       counts per level
* | stats by (service) count() | sort by (count) desc
```

Docs: https://docs.victoriametrics.com/victorialogs/logsql/

## Operating it

- **Relay**: `GET /healthz`, `GET /metrics` (Prometheus text: envelopes accepted/unauthorized/invalid, logs received/written/dropped, queue depth,
  forwards ok/rejected/dropped). Forwarding runs in the background: a slow or down GlitchTip never delays apps or logs.
  Its own logs are collected by Vector (`service:freelog-relay`). Projects/keys are read at startup, so restart after editing `relay.json`.
- **Expose only the relay** to other hosts. VictoriaLogs and Grafana bind to `127.0.0.1`; put a reverse proxy with auth in front for remote access.
- **Known gap (Docker)**: Vector's `docker_logs` source keeps no checkpoint. Lines written while Vector itself is stopped
  (e.g. during an upgrade) are not collected, although they remain in `docker logs`. Config changes hot-reload, so they don't cause a restart.
  Kubernetes tails files with checkpoints and doesn't have this gap.
- **SDK path buffering**: logs are batched in-process. A hard crash can lose the last batch; console output at `Warning+` covers that.

## Releases

Push a tag to publish:

```sh
git tag v0.1.0 && git push origin v0.1.0     # v0.2.0-beta.1 etc. are published as prereleases
```

The release workflow then:
- publishes **FreeLog.Logging** to GitHub Packages, and to nuget.org once Trusted Publishing is set up (below)
- pushes the relay image to **`ghcr.io/reetus/freelog-relay:<version>`** (and `:latest` for non-prereleases), amd64 + arm64
- creates a GitHub release with the `.nupkg` attached

To use the published relay image instead of building it, replace `build:` with `image: ghcr.io/reetus/freelog-relay:<version>` in `deploy/compose/docker-compose.yml`.

Consuming the package from GitHub Packages needs a token even though the repo is public
(a classic PAT with `read:packages`):

```sh
dotnet nuget add source https://nuget.pkg.github.com/Reetus/index.json -n reetus -u <github-user> -p <token> --store-password-in-clear-text
```

nuget.org needs no auth for consumers, so publish there if other teams will use the package.

### Publishing to nuget.org (Trusted Publishing, no stored API key)

One-time setup:
1. Sign in at https://www.nuget.org (a Microsoft account). Your **profile name** is the username, not your email.
2. Username menu → **Trusted Publishing** → add a policy: owner = you, repository owner `Reetus`, repository `freelog`,
   workflow file `release.yml`, environment empty.
3. In GitHub: repo **Settings → Secrets and variables → Actions → Variables** → `NUGET_USER` = your nuget.org profile name.

The release workflow then requests a 1-hour API key via OIDC (`NuGet/login`) and pushes the `.nupkg` and `.snupkg`.
Remove `NUGET_USER` to stop publishing there.

## Development

```sh
dotnet test                       # relay + end-to-end test with the real Sentry SDK
sh deploy/vector/test.sh          # Vector unit tests: Docker pipeline + shared VRL (needs Docker)
sh deploy/k8s/test.sh             # Vector unit tests: Kubernetes pipeline
bash deploy/compose/smoke-test.sh # full stack + demo apps on alternate ports, checks both paths end to end
```

```
src/FreeLog.Relay/        Sentry envelope → VictoriaLogs relay (ASP.NET minimal API, Dockerfile)
src/FreeLog.Logging/      AddFreeLog() for apps
tests/FreeLog.Relay.Tests/
samples/DemoApp/          log generator used by the compose `demo` profile
deploy/compose/           docker-compose stack, Vector config, Grafana provisioning
deploy/vector/            shared VRL normalization + tests
deploy/k8s/               Helm values + Vector agent config
deploy/docker/daemon.json log rotation for Docker hosts
```

Ideas for later: [IDEAS.md](IDEAS.md). Original design notes: [PLAN.md](PLAN.md).

## License

[MIT](LICENSE)
