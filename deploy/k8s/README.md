# freelog on Kubernetes

Vector runs as a DaemonSet that tails `/var/log/pods` (what `kubectl logs` reads) with checkpoints.
VictoriaLogs runs as a single StatefulSet. The relay is optional in-cluster; most setups run it next to
VictoriaLogs and expose it to apps.

Two ways to install: **plain manifests** (no Helm needed) or the **Helm charts**.

## Option 1: plain manifests (`manifests/`)

VictoriaLogs (StatefulSet, 12Gi volume, 14d / 10GiB retention), Vector (DaemonSet + RBAC) and the relay.

```sh
# 1. Relay projects/keys (keys: openssl rand -hex 16); see deploy/compose/relay.example.json
kubectl create namespace freelog
kubectl -n freelog create secret generic freelog-relay-config --from-file=relay.json=./relay.json

# 2. Everything else
kubectl kustomize --load-restrictor LoadRestrictionsNone deploy/k8s/manifests | kubectl apply -f -
```

The relay image defaults to `ghcr.io/reetus/freelog-relay:latest`, which is published by the release workflow.
For a node-specific setup (pinning, a locally built image, the Secret from a file), add an overlay:

```yaml
# deploy/k8s/overlays/<name>/kustomization.yaml
resources: [../../manifests]
namespace: freelog
images:
  - { name: ghcr.io/reetus/freelog-relay, newName: freelog-relay, newTag: local }
secretGenerator:
  - { name: freelog-relay-config, files: [relay.json] }   # relay.json is gitignored
```

Apps in the cluster send to `http://<key>@relay.freelog.svc:8080/<projectId>`.

## Option 2: Helm charts

```sh
kubectl create namespace freelog

# VictoriaLogs
helm repo add vm https://victoriametrics.github.io/helm-charts/
helm upgrade --install victorialogs vm/victoria-logs-single -n freelog -f deploy/k8s/victorialogs-values.yaml
kubectl -n freelog get svc victorialogs        # must exist: vector-agent.yaml sends to victorialogs.freelog.svc:9428

# Vector agent (config + shared VRL come from this repo)
kubectl -n freelog create configmap freelog-vector-config --from-file=deploy/k8s/vector-agent.yaml \
  --dry-run=client -o yaml | kubectl apply -f -
kubectl -n freelog create configmap freelog-vector-vrl --from-file=deploy/vector/normalize.vrl \
  --dry-run=client -o yaml | kubectl apply -f -
helm repo add vector https://helm.vector.dev
helm upgrade --install vector vector/vector -n freelog -f deploy/k8s/vector-values.yaml
```

Re-run the two `configmap` commands after editing the config; Vector reloads it (`VECTOR_WATCH_CONFIG`).
ConfigMap updates take up to ~1 minute to reach the pods.

## Per-pod controls (pod labels)

| Label | Effect |
|---|---|
| `freelog.collect: "false"` | don't collect this pod (e.g. it ships via the Sentry SDK) |
| `freelog.service: <name>` | service name (default: `app.kubernetes.io/name`, then `app`, then container name) |
| `freelog.env: <name>` | environment (default: the agent's `FREELOG_ENV`) |

`kube-system` is excluded in `vector-agent.yaml` (`extra_field_selector`).

## Test the pipeline without a cluster

```sh
sh deploy/k8s/test.sh
```

## Viewing logs

```sh
kubectl -n freelog port-forward svc/victorialogs 9428:9428
# open http://localhost:9428/select/vmui
```
