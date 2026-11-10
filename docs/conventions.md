# Log field conventions

Both ingestion paths (Vector for container output, freelog-relay for Sentry SDK logs) write these fields,
so queries work the same regardless of how a log arrived.

| Field | Example | Set by |
|---|---|---|
| `_time` | `2026-11-10T10:00:00.123Z` | container runtime timestamp / SDK timestamp |
| `_msg` (`message`) | `Order 42 placed by alice` | rendered message |
| `level` | `trace` `debug` `info` `warn` `error` `fatal` | normalized from MEL/Serilog/Sentry/plain-text levels |
| `service` | `orders-api` | label `freelog.service` → compose service / `app.kubernetes.io/name` → container name; relay: project `Service` or `FreeLog:Service` |
| `env` | `production` | label `freelog.env` → agent `FREELOG_ENV`; relay: SDK environment → project `Environment` |
| `source` | `container` / `sentry-sdk` | which path ingested it |
| `trace_id`, `span_id` | 32 / 16 hex chars | SDK; or JSON fields `trace_id`/`TraceId`/`@tr` |
| `category.name` | `Orders.Checkout` | MEL category / Serilog `SourceContext` |
| `message_template` | `Order {OrderId} placed by {Customer}` | MEL `{OriginalFormat}` / Serilog `@mt` |
| `exception.type`, `exception.message` | `System.InvalidOperationException` | SDK path |
| `exception.stacktrace` | full `exception.ToString()` | SDK path; JSON `exception`/`Exception`/`@x` |
| `host` | `docker-host-1` / node name | container path |
| `container_name`, `image`, `compose_project`, `namespace`, `pod` | | container path |
| `release` | `orders@1.2.3` | SDK path |
| `kind` | `metric` | only on metric records (`SentrySdk.Metrics`); logs have no `kind` |
| `metric.name`, `metric.type`, `metric.value`, `metric.unit` | `checkout.duration`, `distribution`, `125.5`, `millisecond` | metric records |
| *anything else* | `OrderId`, `RequestId`, … | template parameters, scopes, extra JSON fields |

Stream fields (VictoriaLogs' grouping key, keep them low-cardinality): `service`, `env`, plus `host` (Docker) or `namespace` (k8s).

## Rules for app authors

- Use message templates, not string interpolation: `LogInformation("Order {OrderId} placed", id)` makes `OrderId` a searchable field.
- Put per-request context in `BeginScope` (`RequestId`, `TenantId`) instead of repeating it in every message.
- Log exceptions as the exception argument: `LogError(ex, "...")`, not `LogError(ex.Message)`.
- On the console path, keep one event per line (JSON). Multi-line output is merged only when continuation lines are indented.
- **No secrets or personal data** in messages or properties: tokens, passwords, card numbers, full emails. Everyone with log access can read them, and they're kept for the full retention period.
