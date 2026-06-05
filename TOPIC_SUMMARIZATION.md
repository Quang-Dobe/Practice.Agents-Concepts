# Service Mesh

A service mesh is a dedicated networking layer that handles the communication between microservices for you, so every service gets encryption, retries, load balancing, and tracing without changing a line of application code. It does that by splitting itself in two: a data plane of small proxies that quietly sit next to each service and carry the real traffic, and a control plane that hands those proxies their rules and certificates from the side.

It matters because once a system has ten or more microservices, every team ends up re-implementing the same plumbing — TLS, retries, timeouts, circuit breakers, request tracing — slightly differently, in whatever language they happen to use. A mesh extracts that plumbing out of the apps and pushes it down into the infrastructure, so policy and observability become a platform feature instead of a library you have to upgrade in forty repos. Engineers reach for it when they need zero-trust mTLS between services, progressive delivery like canary releases, or consistent telemetry across a polyglot fleet, and they avoid it when they only have a handful of services or no platform team to own the control plane.

Picture a city of microservices, where each service is a building. Without a mesh, every building hires its own security guard, postal worker, and translator, and they all do the job slightly differently. With a mesh, the city installs a standardized concierge right outside every building's front door. Your app talks to its concierge in plain language; the concierges talk to each other in encrypted, logged, retry-capable conversations, all following the same rulebook handed down from City Hall.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/cloud/service-mesh/
