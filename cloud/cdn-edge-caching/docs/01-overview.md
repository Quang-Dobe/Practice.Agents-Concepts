# CDN Edge Caching — Overview

> Storing copies of your website's content on hundreds of servers around the world so the user's request never has to travel all the way back to your origin.

## The 30-second version

A CDN (Content Delivery Network) is a fleet of servers spread across the globe. **Edge caching** is the trick that makes a CDN useful: each of those servers keeps a local copy of your content — HTML, images, JS bundles, video chunks, sometimes even API responses — so users get served from a machine that is geographically near them instead of from your single origin server. The result is lower latency for the user and dramatically less traffic hitting your backend. If you have ever wondered why a website in Sydney feels just as snappy as one in San Francisco, this is the mechanism.

## The mental model

Picture a popular cookbook. The **origin** is the one author who wrote it, living in a single city. Without a CDN, every reader in the world has to fly to that city, knock on the author's door, and ask for a photocopy. Slow, and the author burns out fast.

A CDN puts a **library branch** (an *edge POP* — Point of Presence) in every major city. The first reader in Tokyo who asks for chapter 3 triggers a flight to the author (a **cache miss**), but the librarian photocopies the chapter and shelves it. Every subsequent Tokyo reader gets it instantly from the local shelf (a **cache hit**). The author only sees one request per city per chapter, instead of millions.

The librarian needs a rule for how long a shelved copy stays valid before they re-check with the author. That rule is the **TTL** (time-to-live), and it is set by you — usually through HTTP headers like `Cache-Control: max-age=86400` on the response from your origin. Short TTL means fresher content but more origin trips; long TTL means faster delivery but staler content. The whole craft of edge caching is dialing that knob per content type.

## What it is NOT

- **Not a load balancer.** A load balancer spreads traffic across *your* servers. A CDN keeps traffic from ever reaching them.
- **Not edge compute.** Edge functions (Cloudflare Workers, Lambda@Edge) run *code* at the edge; edge caching just serves stored *bytes*. They often live in the same product, but they are different primitives.
- **Not browser caching.** The browser cache lives on one user's laptop. The edge cache is shared across every user routed to that POP.
- **Not a database replica.** A CDN does not run queries — it serves opaque HTTP responses it was told it could keep.

## When you would reach for it

- You serve static assets (images, video, CSS, JS, fonts) to users in more than one region.
- Your origin is getting hammered by repeated identical requests.
- You want faster page loads without rewriting your app.
- You need to absorb traffic spikes (launches, viral moments, DDoS-ish bursts) without scaling the origin.
- You serve large files (installers, video segments) where bandwidth from origin would be expensive.

## When you would NOT reach for it

- The response is unique per user on every request (personalized dashboards, authenticated API calls) — cache hit ratio will be near zero.
- Strong real-time consistency is mandatory (live trading prices, inventory at checkout).
- Your audience is geographically tiny and your origin is already next door to them.
- Content is so rarely requested that the cache evicts it between hits — you pay the miss cost every time.

## Key vocabulary (just enough to keep reading)

- **Origin** — your actual backend server, the source of truth.
- **Edge / POP** — a CDN server in a specific geographic location.
- **Cache hit** — the edge had the content; user gets it fast.
- **Cache miss** — the edge had to ask the origin first.
- **TTL** — how long a cached object is considered fresh.
- **Cache-Control** — the HTTP header your origin uses to tell the edge what it may cache and for how long.
- **Cache key** — the identifier (usually URL plus a few headers) the edge uses to decide whether two requests want the same content.
- **Static content** — bytes that are identical for every user; caches beautifully.
- **Dynamic content** — bytes that depend on the user or moment; caches poorly without extra work.
- **Hit ratio** — the percentage of requests served from cache; 95%+ is healthy for static-heavy sites.

## What's next

The next document (`02-deep-dive.md`) answers the **What / Where / When / How / Why** in detail: the request lifecycle through a POP, cache key construction, purging and invalidation, signed URLs, and the dynamic-content techniques (stale-while-revalidate, edge-side includes, tiered caching) that turn a naive setup into a production-grade one.
