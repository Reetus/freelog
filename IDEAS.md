# Ideas for later

Not planned yet; notes so they aren't lost.

## ~~GlitchTip: exception grouping and alerts~~ (done 2026-11-10, see README "Error tracking")

[GlitchTip](https://glitchtip.com) is a free, open-source (MIT), self-hosted error tracker that accepts the Sentry SDK protocol.
It groups identical exceptions into issues (counts, first/last seen, affected releases) and sends alerts (email, Slack, webhooks).
It needs only Postgres + Redis/Valkey and runs in roughly 1 GB RAM, unlike self-hosted Sentry (Kafka, ClickHouse, ~16 GB).

How it would plug in:
- Relay: forward non-`log` envelope items (`event`, `transaction`, …) to a GlitchTip DSN per project instead of dropping them.
  The relay already parses item types; this is a per-project `ForwardDsn` setting plus an HTTP forwarder.
- FreeLog.Logging: set `MinimumEventLevel = LogLevel.Error` (currently `None`) so errors also become Sentry events.
- GlitchTip ≥ 6.1 can also store logs itself, but in Postgres with limited attribute search, so VictoriaLogs stays the log store.

## Others

- **Alerting on logs**: Grafana alert rules on LogsQL queries (e.g. `service:X level:error | stats count()` > N in 5m), or `vmalert` with VictoriaLogs.
- **Auth proxy**: Caddy/Traefik with basic auth or OAuth2-proxy in front of VictoriaLogs UI and Grafana for remote access.
- **Checkpointed Docker collection**: switch the Docker host path from the `docker_logs` API source to tailing `json-file` logs
  (`log-opts: tag={{.Name}}, labels=...`) so nothing is lost while Vector restarts.
- **Relay hot reload** of `relay.json` (projects/keys) without a restart.
- **OTLP ingest**: VictoriaLogs accepts OpenTelemetry logs natively, so non-.NET or non-Sentry apps can use an OTel exporter with no relay.
- **Browser/JS apps**: CORS on the relay so the Sentry browser SDK can send logs directly.
- **Retention tiers in compose**: a second VictoriaLogs service (e.g. `audit`, 1y) wired to relay projects via `VictoriaLogsUrl`.
