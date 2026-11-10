# freelog — self-hosted log pipeline plan

> **Status (2026-11-10): implemented.** See README.md for usage. Changes from this plan made during implementation:
> - `FreeLog.Logging` uses its own `ILogger` provider (`FreeLogLoggerProvider`) over `SentrySdk.Logger` instead of the SDK's
>   `EnableLogs` integration, which in 6.12 drops exceptions and scopes (found by the end-to-end test).
> - GlitchTip: first dropped, then added as an optional compose profile. The relay forwards errors per project (`ForwardDsn`),
>   and FreeLog.Logging sets fingerprints so IDs in exception messages don't split issues.
> - Vector ≥ 0.5x no longer expands `${ENV}` in config files by default, so sink URLs are written literally.
> - Vector's `docker_logs` source has no checkpoint: lines written while Vector is stopped aren't collected (documented in README).
> - Container-path fields were normalized to match the relay (`level` values `trace|debug|info|warn|error|fatal`).

Goal: a free replacement for Datadog Logs that
- ingests logs from Docker and Kubernetes **without breaking `docker logs` / `kubectl logs`**
- handles JSON (structured) logs as first-class
- has configurable retention
- is cheap to run (single small VM / a few hundred MB of RAM to start)

---

## 1. Recommendation (TL;DR)

Two ingestion paths into one store, so projects can move from one to the other gradually:

```
 PATH 1 — no code changes (any project, plain text or JSON)
 app ──stdout──▶ container runtime ──▶ local log files ◀── docker logs / kubectl logs (unchanged)
                                            │ (Docker API / file tail, read-only)
                                            ▼
                                         Vector (1 per host / DaemonSet)
                                            │
 PATH 2 — Sentry .NET SDK in-app            │
 app (ILogger → FreeLog.Logging → SDK) ─────┼──▶ freelog-relay  (Sentry envelope endpoint, our code)
        DSN = https://key@freelog/1         │       │  log items ──────────────┐
                                            │       │  other item types ──▶ dropped
                                            ▼       ▼                          │
                                         VictoriaLogs  ◀───────────────────────┘
                                         (storage + query + retention + UI) ──▶ Grafana (optional)
```

| Concern | Choice | Why |
|---|---|---|
| Collector | **Vector** (Rust, Apache-2.0) | One binary, native `docker_logs` and `kubernetes_logs` sources, JSON parsing via VRL, disk buffers, ~20–50 MB RAM. Fluent Bit is an equally valid alternative. |
| Storage | **VictoriaLogs** (Apache-2.0) | Purpose-built for exactly this: schemaless JSON ingestion, every field indexed, 10–50× compression, single binary, global `-retentionPeriod` + disk-size cap, built-in web UI, Grafana plugin. Runs in ~100–200 MB RAM. |
| Fallback storage | **ClickHouse** | Use instead if you need **per-service/per-row retention**, SQL, or want to build a custom freelog UI/API on top. |
| Postgres? | **Not recommended** for the log store | See §3. Fine for *metadata* (users, saved searches, alert rules) if we build a UI. |
| C# logging | **Sentry .NET SDK (MIT, free)** via `FreeLog.Logging`, DSN pointed at our own relay — *not* sentry.io | See §4. Console-only projects keep working via Path 1. |
| SDK receiver | **freelog-relay** (small ASP.NET minimal API we write) | Accepts Sentry envelopes, writes `log` items to VictoriaLogs, drops other item types. |

The code we actually write is the relay (a few hundred lines) plus configs and deployment manifests.

---

## 2. Ingestion without replacing `docker logs`

### Docker (standalone / compose)
- Keep a **local** log driver so `docker logs` keeps working: `local` (compressed, recommended) or `json-file`.
- **Turn on rotation** — Docker does not rotate `json-file` by default, which is the #1 way hosts fill their disks.

`/etc/docker/daemon.json` (Docker Desktop: Settings → Docker Engine):
```json
{
  "log-driver": "local",
  "log-opts": { "max-size": "20m", "max-file": "5" }
}
```
> Applies to newly created containers only — recreate existing ones.

- Vector's `docker_logs` source reads via the Docker API (mounted `/var/run/docker.sock`, read-only). It is a passive reader: nothing about the app's logging changes.
- Since Docker 20.10 "dual logging" also keeps `docker logs` working even if you switch to a remote driver — but we don't need that; a sidecar reader is simpler and survives collector outages (logs stay on disk, Vector resumes).

### Kubernetes
- kubelet/CRI already writes container stdout to `/var/log/pods/...` (symlinked in `/var/log/containers/`). `kubectl logs` reads those — we never touch them.
- Deploy Vector as a **DaemonSet** (official Helm chart, `role: Agent`) with the `kubernetes_logs` source. It tails those files, merges CRI partial lines, and enriches with namespace/pod/container/labels.
- Rotation is kubelet's job (`containerLogMaxSize`, `containerLogMaxFiles`).

### Delivery guarantees
- Vector checkpoints file offsets → restarts don't duplicate or lose much.
- Enable `buffer.type: disk` on the sink so a storage outage doesn't drop logs.

---

## 3. Storage choice: why not Postgres

Postgres *can* work, but for logs it is the wrong shape:

| | Postgres (JSONB) | ClickHouse | VictoriaLogs |
|---|---|---|---|
| Compression | ~1–2× (TOAST) | ~10–20× | ~10–50× |
| Retention | Partition by day + drop partitions (pg_partman / cron). Per-service retention = DELETE jobs + bloat/vacuum | Native `TTL` clause, **per-row** expressions | Global `-retentionPeriod` + `-retention.maxDiskSpaceUsageBytes` |
| Free-text search | GIN/trigram indexes, expensive on write & disk | token bloom-filter skip indexes, fast | built-in, all fields indexed |
| Write path | row-at-a-time WAL, index maintenance | batched columnar inserts | batched, designed for this |
| Ops | familiar | moderate (1–2 GB RAM to be comfortable) | trivial (single binary, no schema) |
| Comfortable ceiling on a small VM | ~1–2 GB logs/day | 100s of GB/day | 100s of GB/day |

Decision: **VictoriaLogs** by default. Switch to **ClickHouse** if per-service retention or SQL is a hard requirement.

### Retention options
- **VictoriaLogs:** `-retentionPeriod=14d` (global) and `-retention.maxDiskSpaceUsageBytes=50GB` (safety cap). For different retention tiers (e.g. audit logs 1y, debug 3d), run a second instance and route by field in Vector — cheap since each is a tiny process.
- **ClickHouse:** per-row retention driven by a column Vector sets from config:

```sql
CREATE TABLE logs
(
    ts             DateTime64(3)              CODEC(Delta, ZSTD),
    service        LowCardinality(String),
    env            LowCardinality(String),
    level          LowCardinality(String),
    host           LowCardinality(String),
    container      LowCardinality(String),
    trace_id       String                     CODEC(ZSTD),
    message        String                     CODEC(ZSTD),
    attrs          Map(LowCardinality(String), String) CODEC(ZSTD),  -- or JSON type on CH >= 25.3
    retention_days UInt16 DEFAULT 14,
    INDEX idx_msg message TYPE tokenbf_v1(32768, 3, 0) GRANULARITY 4
)
ENGINE = MergeTree
PARTITION BY toDate(ts)
ORDER BY (service, level, ts)
TTL toDateTime(ts) + toIntervalDay(retention_days) DELETE;
```

---

## 4. C#: the Sentry SDK path

Sentry's .NET SDK ships structured logs since **5.14.0** (`EnableLogs = true`; the Microsoft.Extensions.Logging integration then forwards `ILogger` calls). The SDK is MIT-licensed — Sentry only costs money if you send to sentry.io. Self-hosted Sentry is too heavy (Kafka, ClickHouse, Redis, ~16 GB RAM), so we point the DSN at our own receiver.

```csharp
// packages: Sentry.AspNetCore (>= 5.14)
builder.WebHost.UseSentry(o =>
{
    o.Dsn = builder.Configuration["Sentry:Dsn"];   // https://<key>@logs.internal/1  (our relay)
    o.EnableLogs = true;                            // ILogger → Sentry log items
    o.Environment = builder.Environment.EnvironmentName;
    o.CacheDirectoryPath = "/tmp/sentry";           // keep envelopes on disk if relay is down
});
// Direct API also available: SentrySdk.Logger.LogInfo(...), LogWarning(...), LogError(...)
```

### Wire format the relay must accept
The SDK POSTs to `/api/{projectId}/envelope/` with `X-Sentry-Auth: Sentry sentry_key=...`, usually gzip-compressed. Body is newline-delimited:

```
{"sdk":{"name":"sentry.dotnet","version":"..."}}                                     ← envelope header
{"type":"log","item_count":2,"content_type":"application/vnd.sentry.items.log+json"} ← item header
{"items":[{"timestamp":1731200000.125,"trace_id":"0af7…","level":"info","body":"User alice logged in",
           "severity_number":9,"attributes":{"user":{"value":"alice","type":"string"}}}, …]}
```
- Max one `log` item per envelope, ≤100 entries.
- Item headers may carry `length` → read exactly that many bytes; otherwise read to newline.
- Other item types (`event`, `transaction`, `client_report`, …): return 200 and drop.

### freelog-relay responsibilities
1. Validate `sentry_key` against a key → `{project, service, retention tier}` map in config.
2. Decompress (gzip / br / deflate), parse envelope.
3. Flatten each log entry: `body`→`_msg`, `timestamp`→`_time`, `level`, `trace_id`, `span_id`, `attributes.*.value`→top-level fields, plus `service`/`env`/`sdk`.
4. Batch and POST as JSON lines to VictoriaLogs `/insert/jsonline` (or ClickHouse).
5. Always return 200 quickly; use a bounded in-memory queue + retry. Expose `/healthz` and basic counters.

### Why not just use GlitchTip for logs?
GlitchTip ≥ 6.1 accepts Sentry log envelopes natively, and that's a valid shortcut if volume is low. But its log storage is Postgres (7 days hot by default, then optional Parquet archive via `GLITCHTIP_LOG_HOT_DAYS` / `GLITCHTIP_LOG_RETENTION_DAYS`), its documented search is level/service/env + full-text rather than arbitrary attributes, and container stdout logs (Path 1) wouldn't land in the same place. Decision: no GlitchTip for now (see IDEAS.md).

### Caveats of the SDK path (vs stdout)
- Logs are buffered in-process: a hard crash can lose the last batch. Keep console output too, at least for `Warning+`, so `docker logs` still shows crashes.
- **Avoid duplicates:** containers that ship via the SDK get the label `freelog.collect=false`, and Vector skips them (or Vector keeps only stderr for them).
- If the relay is unreachable the app keeps running; `CacheDirectoryPath` covers short outages.

### Alternative: no SDK, stdout JSON
For projects you don't want to add Sentry to, or non-ASP.NET apps, structured stdout still works through Path 1. Projects that aren't on JSON yet still work too — they show up as plain-text messages.

Option A — zero dependencies (Microsoft.Extensions.Logging):
```csharp
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;          // scopes → structured fields (e.g. RequestId, TraceId)
    o.UseUtcTimestamp = true;
    o.TimestampFormat = "O";
    o.JsonWriterOptions = new() { Indented = false }; // MUST be one line per event
});
```

Option B — Serilog (nicer enrichers, more control over field names):
```csharp
// packages: Serilog.AspNetCore, Serilog.Formatting.Compact
builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service", "my-api")
    .WriteTo.Console(new Serilog.Formatting.Compact.RenderedCompactJsonFormatter()));
```

**Non-containerised apps** (Windows services, desktop, IIS) are where the SDK path is most useful, since there's no runtime capturing stdout. The vendor-neutral equivalent is OpenTelemetry (`OpenTelemetry.Exporter.OpenTelemetryProtocol` → VictoriaLogs' OTLP endpoint), which needs no relay. Pick Sentry if the projects already use or will use it for errors; otherwise OTLP is less code.

### Log field conventions (all services)
| Field | Example | Notes |
|---|---|---|
| `timestamp` | ISO-8601 UTC | |
| `level` | `Information` | normalize in Vector to `info/warn/error/...` |
| `message` | rendered text | becomes `_msg` in VictoriaLogs |
| `service`, `env` | `orders-api`, `prod` | set via env vars or k8s labels |
| `trace_id`, `span_id` | from `Activity.Current` | lets you correlate later with tracing |
| `exception` | full stack trace as **one string field** | never multi-line raw output |

---

## 5. Vector config sketch (Docker host)

```yaml
# vector.yaml
data_dir: /var/lib/vector

sources:
  docker:
    type: docker_logs
    exclude_containers: [vector, victorialogs]

transforms:
  parse:
    type: remap
    inputs: [docker]
    source: |
      # Lift JSON logs into structured fields; leave plain-text lines as-is.
      if is_string(.message) {
        parsed, err = parse_json(.message)
        if err == null && is_object(parsed) {
          . = merge!(., parsed)
        }
      }
      .service = .service || .label."com.docker.compose.service" || .container_name
      .level = downcase(string(.level || .Level || "info") ?? "info")

sinks:
  vlogs:
    type: elasticsearch           # VictoriaLogs speaks the ES bulk API
    inputs: [parse]
    endpoints: ["http://victorialogs:9428/insert/elasticsearch/"]
    api_version: v8
    compression: gzip
    healthcheck: { enabled: false }
    query:
      _msg_field: message
      _time_field: timestamp
      _stream_fields: host,service
    buffer:
      type: disk
      max_size: 1073741824        # 1 GiB
```

For k8s the only change is `type: kubernetes_logs` for the source and `_stream_fields: kubernetes.pod_namespace,kubernetes.container_name`.

---

## 6. Phased plan

**Phase 0 — prerequisites (½ day)**
- Set log rotation on every Docker host (`daemon.json`) and confirm kubelet log rotation in k8s.
- Decide initial retention (suggest 14d) and disk cap.

**Phase 1 — single-host MVP (1 day)**
- `deploy/compose/docker-compose.yml`: VictoriaLogs (volume, `-retentionPeriod`, disk cap) + Vector (docker.sock ro) + optional Grafana.
- Verify: `docker logs <app>` unchanged; logs searchable in VictoriaLogs UI (`:9428/select/vmui`); JSON fields queryable (`service:orders-api level:error`).

**Phase 2 — freelog-relay (2–3 days)**
- ASP.NET minimal API: envelope endpoint, key map, decompression, envelope parser, VictoriaLogs jsonline writer, bounded queue + retry.
- Tests: golden envelopes captured from a real `Sentry.AspNetCore` app (gzip + uncompressed, with/without `length`), mixed item types.
- Container image + add to compose.

**Phase 3 — C# services (1–2 days)**
- Shared `FreeLog.Logging` package: `AddFreeLog()` = Sentry SDK + own ILogger provider + DSN from config + console sink for `Warning+`.
- Roll out service by service; label SDK containers `freelog.collect=false`; verify no duplicates and `trace_id` present.

**Phase 4 — Kubernetes (1 day)**
- Vector Helm chart (`role: Agent`) with `kubernetes_logs` → central VictoriaLogs (Helm chart `victoria-logs-single`, or the compose instance if reachable).
- Exclude noisy namespaces (`kube-system`) or sample them.

**Phase 5 — hardening (1–2 days)**
- Auth: VictoriaLogs has no built-in auth → put it behind a reverse proxy (Caddy/Traefik basic auth or OAuth2-proxy); keep `:9428` off the public internet.
- Disk buffers, Vector `internal_metrics` → alert if the pipeline stalls.
- Backups are optional for logs; snapshot the volume if you need audit retention.
- Tiered retention if needed (second instance or ClickHouse switch).

**Phase 6 — optional extras**
- Grafana dashboards + alerting on log queries (error rate per service).
- See IDEAS.md (GlitchTip, alerting, auth proxy, …).
- OTLP traces later (Tempo/Jaeger) using the `trace_id` already in logs.

---

## 7. Repo layout (proposed)

```
freelog/
  PLAN.md
  deploy/
    compose/            docker-compose.yml, vector.yaml, .env.example
    k8s/                vector-values.yaml, victorialogs-values.yaml
    docker/daemon.json
  src/
    FreeLog.Relay/      Sentry envelope → VictoriaLogs
    FreeLog.Relay.Tests/
    FreeLog.Logging/    shared AddFreeLog() extension (SDK + ILogger provider + console fallback)
  docs/
    conventions.md      field names, levels, PII rules
```

## 8. Open questions
1. Expected volume (GB/day) and number of hosts / clusters? Determines whether one VictoriaLogs node is enough (almost certainly yes under ~50 GB/day).
2. Do different services need different retention? If yes and more than 2–3 tiers → ClickHouse.
3. Which projects already reference the Sentry SDK, and on which version? Logs need ≥ 5.14.
4. ~~GlitchTip~~ — decided: not now, see IDEAS.md.
