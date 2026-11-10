# freelog on Kubernetes

Vector runs as a DaemonSet that tails `/var/log/pods` (what `kubectl logs` reads) with checkpoints.
VictoriaLogs runs as a single StatefulSet. The relay is optional in-cluster; most setups run it next to
VictoriaLogs and expose it to apps.

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
