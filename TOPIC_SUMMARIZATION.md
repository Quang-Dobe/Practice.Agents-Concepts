# Sidecar Pattern

The sidecar pattern attaches a small helper container to your application so they run together as one deployment unit, sharing network and local filesystem. The helper takes over chores that are not your application's actual job — shipping logs, terminating TLS, fetching short-lived secrets, retrying failed downstream calls — while your code stays focused on what the service is for.

Engineers reach for it any time the same cross-cutting plumbing would otherwise be duplicated inside dozens of microservices. Standardizing it in a sidecar means a platform team can upgrade log shipping, rotate certificates, or roll out mutual TLS across a whole fleet without touching application code. This is exactly what service meshes like Istio and Linkerd do — every Pod gets an Envoy proxy injected as a sidecar, and that proxy is what actually talks to the network. Kubernetes made native sidecar containers a first-class concept in version 1.29 and promoted them to general availability in 1.33, so the lifecycle guarantees that used to require workarounds are now built in.

A useful picture is a motorcycle with a sidecar attached. The motorcycle is your app — fast, focused, going one place. The sidecar cabin goes everywhere the motorcycle goes, shares its fuel and destination, and carries the cargo the rider does not want to deal with: the GPS, the radio, the passenger. In Kubernetes terms, that is a single Pod with two containers sharing an IP and a volume, living and dying together, with the app talking to `localhost` and the sidecar handling the messy outside world.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/cloud/sidecar-pattern/
