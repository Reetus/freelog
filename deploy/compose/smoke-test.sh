#!/usr/bin/env bash
# End-to-end smoke test: starts the stack plus demo apps as a separate compose project ("freelog-smoke")
# on alternate ports, then checks that logs from both ingestion paths are queryable in VictoriaLogs.
# Safe to run next to a local stack. Needs Docker, curl and python3.
set -euo pipefail
cd "$(dirname "$0")"

export COMPOSE_PROJECT_NAME=freelog-smoke
export FREELOG_RELAY_CONFIG=./relay.smoke.json
export FREELOG_RELAY_PORT=${FREELOG_RELAY_PORT:-15080}
export FREELOG_UI_PORT=${FREELOG_UI_PORT:-19428}
VL="http://127.0.0.1:${FREELOG_UI_PORT}"
COMPOSE=(docker compose --env-file /dev/null --profile demo)

cleanup() {
  if [ "${KEEP:-0}" != "1" ]; then "${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true; fi
}
trap cleanup EXIT

"${COMPOSE[@]}" up -d --build --quiet-pull

count() { # $1 = LogsQL query → number of matching records in the last 10 minutes
  curl -sf "$VL/select/logsql/query" --data-urlencode "query=_time:10m $1 | stats count() n" \
    | python3 -c 'import sys,json; l=sys.stdin.read().strip(); print(json.loads(l)["n"] if l else 0)' 2>/dev/null || echo 0
}

check() { # $1 = description, $2 = query
  for _ in $(seq 1 90); do
    if [ "$(count "$2")" -gt 0 ]; then echo "ok   $1"; return 0; fi
    sleep 2
  done
  echo "FAIL $1   (query: $2)"; return 1
}

failed=0
check "SDK path: structured log with template fields"   'service:demo-sdk source:sentry-sdk OrderId:* RequestId:*' || failed=1
check "SDK path: exception fields on errors"            'service:demo-sdk level:error exception.type:*'           || failed=1
check "Console path: JSON fields extracted by Vector"   'service:demo-console source:container OrderId:* RequestId:*' || failed=1
check "Console path: exception stack trace as a field"  'service:demo-console level:error exception.stacktrace:InvalidOperationException' || failed=1
check "Relay's own logs collected"                       'service:freelog-relay'                                   || failed=1

if [ "$failed" != 0 ]; then
  echo "--- relay logs";  "${COMPOSE[@]}" logs --tail 30 relay  || true
  echo "--- vector logs"; "${COMPOSE[@]}" logs --tail 30 vector || true
  curl -s "http://127.0.0.1:${FREELOG_RELAY_PORT}/metrics" | grep -v '^#' || true
  exit 1
fi
echo "smoke test passed"
