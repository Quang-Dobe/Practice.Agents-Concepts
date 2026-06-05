# Service Mesh - MVP Code

The smallest runnable demo of a service mesh enforcing **identity-based authorization** with automatic mTLS. About 90 lines of YAML, comments excluded.

## What it demonstrates

- The mesh injects a sidecar proxy into every pod via a namespace label (`linkerd.io/inject=enabled`) - the app images are unchanged.
- A `Server` resource flips the default for one port from "open" to "deny all" - a real-world default-deny posture (practice doc, best practice #1 and checklist "default-deny in production").
- An `AuthorizationPolicy` + `MeshTLSAuthentication` whitelists exactly one client identity (`allowed-client-sa`). Identical traffic from a different `ServiceAccount` is rejected by the inbound proxy with 403, never reaching nginx.
- mTLS is automatic - you never wrote a cert, yet both legs of the call are encrypted with rotating SPIFFE-bound certs.

## Prerequisites

- A local Kubernetes cluster: `kind create cluster` (or k3d, minikube)
- `kubectl`
- The Linkerd CLI: `curl -sL https://run.linkerd.io/install-edge | sh && export PATH=$HOME/.linkerd2/bin:$PATH`

## Run it

```bash
# 1. Install the control plane (CRDs + istiod-equivalent in linkerd namespace).
linkerd install --crds | kubectl apply -f -
linkerd install | kubectl apply -f -
linkerd check                                  # wait until all green

# 2. Apply the demo. The namespace label triggers sidecar injection.
kubectl apply -f mvp.yaml
kubectl -n mesh-demo rollout status deploy/server deploy/allowed-client deploy/denied-client

# 3. Try both clients hitting the same Service.
kubectl -n mesh-demo exec deploy/allowed-client -c curl -- \
  curl -sS -o /dev/null -w "allowed: HTTP %{http_code}\n" http://server

kubectl -n mesh-demo exec deploy/denied-client -c curl -- \
  curl -sS -o /dev/null -w "denied:  HTTP %{http_code}\n" http://server
```

## Expected output

```
allowed: HTTP 200
denied:  HTTP 403
```

To see the mesh in action live: `linkerd viz install | kubectl apply -f -` then `linkerd viz tap -n mesh-demo deploy/server` while running the curls - you'll see `tls=true` and the `:authority` of each request.

## What to try next

- Delete the `AuthorizationPolicy` and re-run both curls - both now return 403 (the `Server` alone is default-deny).
- Delete the `Server` resource and re-run - both succeed (no `Server` means no authz enforcement on that port).
- Remove the `linkerd.io/inject: enabled` label, recreate the namespace, and watch the policy become a no-op - identity needs the sidecar.
- Add a second `MeshTLSAuthentication` for `denied-client-sa` and reference it from the same `AuthorizationPolicy` - watch denied flip to 200.

## Teardown

```bash
kubectl delete -f mvp.yaml
linkerd uninstall | kubectl delete -f -
kind delete cluster   # if you used kind
```
