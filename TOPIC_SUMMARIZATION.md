# Blue-Green Deployment

Blue-green deployment is a release strategy where you keep two identical copies of your production environment running side by side — one is live and serving users, the other sits idle. You deploy the new version to the idle copy, smoke-test it while real traffic still flows to the old one, then flip a single switch — a load balancer rule, a DNS record, a Kubernetes service selector — that hands all traffic to the new environment. The old environment stays warm in the background as an instant rollback target.

Engineers reach for it when downtime is unacceptable and rollback needs to be measured in seconds, not "rebuild and redeploy." Because the previous version stays running behind the curtain, undoing a bad release is just another flip of the same switch. It pays for itself on payments, checkout, and public APIs where users notice every hiccup. It is the wrong tool when the release contains a schema-breaking database change both colors share, when doubling capacity is genuinely too expensive, or when you actually want to expose the new build gradually to a slice of real users — that last case is what canary releases are for.

Picture a theatre with two identical sets behind a curtain. The audience watches set A while the crew quietly dresses set B for the next act. When set B is ready, the curtain shifts and the audience is now looking at B — set A is still standing, lights on, ready to come back if a prop falls. Nothing is rebuilt in front of a live audience, and rolling back is a curtain pull, not a renovation.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/cloud/blue-green-deployment/
