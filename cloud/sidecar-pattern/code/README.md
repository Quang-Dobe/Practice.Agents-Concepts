# Sidecar Pattern — MVP Code

A single Pod with two containers sharing an `emptyDir`. The app writes log lines to a file; a Fluent Bit sidecar tails that file and parses them. Uses the Kubernetes 1.29+ native sidecar declaration (`initContainers` entry with `restartPolicy: Always`, KEP-753) so the sidecar starts before the app and terminates after it.

## Prerequisites

A cluster on **Kubernetes 1.29 or later** (native sidecars are beta-on by default in 1.29, GA in 1.33) and a working `kubectl`. Both work out of the box:

```bash
kind create cluster --image kindest/node:v1.33.0
# or
minikube start --kubernetes-version=v1.33.0
```

## Run it

```bash
kubectl apply -f mvp.yaml
kubectl wait --for=condition=Ready pod/sidecar-demo --timeout=60s
```

## Confirm the sidecar is doing its job

```bash
kubectl exec sidecar-demo -c app -- tail -n 3 /var/log/app/app.log
kubectl logs sidecar-demo -c log-shipper --tail=3
kubectl get pod sidecar-demo -o jsonpath='{.spec.initContainers[0].restartPolicy}'   # Always
```

The first command shows raw app log lines on the shared volume; the second shows the same lines parsed to JSON on the sidecar's stdout; the third proves the native-sidecar wiring is in effect.

## Cleanup

```bash
kubectl delete -f mvp.yaml
```
