#!/usr/bin/env sh
# Runs Vector's unit tests for the Kubernetes pipeline (needs Docker, not a cluster).
set -eu
cd "$(dirname "$0")/.."
MSYS_NO_PATHCONV=1 docker run --rm --label freelog.collect=false \
  -v "$PWD/k8s:/etc/vector:ro" \
  -v "$PWD/vector/normalize.vrl:/vector-vrl/normalize.vrl:ro" \
  -e VECTOR_LOG=warn \
  timberio/vector:0.60.0-debian \
  test /etc/vector/vector-agent.yaml /etc/vector/vector-agent.tests.yaml
