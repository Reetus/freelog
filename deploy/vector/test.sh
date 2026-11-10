#!/usr/bin/env sh
# Runs Vector's unit tests for the Docker pipeline and the shared VRL (needs Docker).
set -eu
cd "$(dirname "$0")/.."
MSYS_NO_PATHCONV=1 docker run --rm \
  -v "$PWD/compose/vector.yaml:/etc/vector/vector.yaml:ro" \
  -v "$PWD/vector:/etc/vector/vrl:ro" \
  -e VECTOR_LOG=warn --label freelog.collect=false \
  timberio/vector:0.60.0-debian \
  test /etc/vector/vector.yaml /etc/vector/vrl/tests.yaml
