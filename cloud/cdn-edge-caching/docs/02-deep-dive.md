# CDN Edge Caching — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

CDN edge caching is the practice of placing HTTP reverse-proxy caches inside the points of presence (POPs) of a geographically distributed delivery network, so that responses from an origin server are stored under a deterministic **cache key** and re-served to subsequent requests whose key matches, subject to a freshness lifetime expressed through HTTP cache-control semantics ([RFC 9111](https://www.rfc-editor.org/rfc/rfc9111) plus the stale-content extensions in [RFC 5861](https://www.rfc-editor.org/rfc/rfc5861.html)).

### The core building blocks

- **POP (Point of Presence)** — a physical site with one or more cache servers (usually Varnish, ATS, nginx, or a vendor's in-house equivalent) terminating TLS and answering user requests.
- **Cache object** — the stored response (headers + body), indexed by cache key, with metadata: insertion time, TTL, surrogate keys, vary axes.
- **Cache key** — the tuple the proxy uses to look the object up. Default: scheme + host + path + (optionally) query. Customizable to include selected headers, cookies, device class.
- **Vary axes** — additional dimensions the cache must split on, declared by the origin via the `Vary` response header (e.g. `Vary: Accept-Encoding`).
- **Freshness lifetime** — how long the object may be served without revalidation, computed from `Cache-Control: s-maxage` → `max-age` → `Expires` → heuristic.
- **Tier** — edge POP, regional shield, or origin shield. Each tier coalesces misses from the tier below.
- **Purge / invalidation channel** — out-of-band API that evicts or marks-stale objects by URL, surrogate key, or wildcard.

### How it relates to the broader landscape

Edge caching is a specialization of HTTP reverse proxying ([Varnish](https://varnish-cache.org/), [Squid](http://www.squid-cache.org/), [nginx](https://nginx.org/) in proxy mode), distinguished by global distribution and an anycast routing layer that maps a user to the nearest POP. Its siblings are browser caching (private, per-user), application-level caches like Redis/Memcached (shared but in-datacenter), and edge compute platforms (Workers, Lambda@Edge) which run code, not just serve bytes. A CDN typically bundles all four — caching, routing, TLS, and compute — into one product.

## Where

### Where it runs / lives in the stack

Between the user's TCP stack and your origin's load balancer. The edge cache is the first piece of *your* infrastructure the request touches (after the user's ISP and the anycast network) and the last piece it touches on the way out. Architecturally it lives at L7 — it parses HTTP, makes decisions on headers, and re-emits modified responses.

### Where you typically encounter it

- **Cloudflare** (full reverse proxy, Workers-integrated cache, default for many sites)
- **Fastly** (Varnish + VCL, surrogate-key purging, popular for news and commerce)
- **Amazon CloudFront** (tight AWS integration, policy-driven cache keys)
- **Akamai** (the original; still dominant in media, OTT video, regulated industries)
- **Bunny.net** (cheap pay-as-you-go, [Perma-Cache](https://support.bunny.net/hc/en-us/articles/360017093479-Understanding-Perma-Cache) as a permanent middle tier)
- **Google Cloud CDN**, **Azure Front Door**, **Vercel/Netlify edge** (PaaS-bundled)

### Ecosystem and tooling

- **Open-source cache engines you'll meet on-prem or self-hosted**: Varnish (used by Fastly), Apache Traffic Server (used historically by Yahoo / Akamai), nginx, HAProxy.
- **Config / IaC**: Terraform providers for each vendor, Cloudflare Wrangler, Fastly's [VCL](https://www.fastly.com/documentation/reference/vcl/) and Compute@Edge.
- **Observability**: per-vendor log streams (Cloudflare Logpush, Fastly Real-Time Analytics, CloudFront standard logs) and `X-Cache: HIT/MISS` style debug headers.
- **Specs to know**: [RFC 9111](https://www.rfc-editor.org/rfc/rfc9111) (HTTP caching), [RFC 5861](https://www.rfc-editor.org/rfc/rfc5861.html) (stale extensions), [Edge Architecture Specification](https://www.w3.org/TR/edge-arch) (ESI), [RFC 8246](https://www.rfc-editor.org/rfc/rfc8246) (`immutable`).

## When

### When the topic emerged and why

Akamai launched in 1999 to solve a concrete problem: the 1998 World Cup site collapsed under load, and a single origin in one datacenter could not feed a global audience without unacceptable RTT. Before CDNs, the workaround was DNS-based mirroring — manually replicated FTP/HTTP servers in different regions. CDNs automated that, layered on consistent invalidation, and over time absorbed TLS termination, DDoS scrubbing, and edge compute. The shift from object caching alone to full-stack edge platforms accelerated after ~2017 with Cloudflare Workers.

### When to use it in a project

Reach for CDN edge caching when:
- You serve any static asset (JS/CSS/images/video/fonts) to more than one region.
- Your origin RPS for a hot path exceeds what a single instance handles comfortably and the responses are *cacheable for at least seconds*.
- You need to absorb spikes (launches, virality, mild DDoS) without scaling origin.
- You serve large files where origin egress would be expensive — most CDNs negotiate transit far cheaper than your cloud bill.
- You need TLS termination, HTTP/3, or Brotli at every region without operating it yourself.

### When NOT to use it

Avoid it when:
- Responses are fully personalized per request with no extractable shared shape (raw GraphQL endpoints for an authenticated dashboard).
- Strong read-after-write consistency is mandatory at sub-second scale (live order book, checkout inventory) — the cache will lie unless you purge synchronously, which is rare.
- Traffic is so low and so geographically concentrated that the cache never warms up (long-tail SaaS internal tool, 50 users in one city, one origin nearby).
- Compliance forbids data leaving certain jurisdictions and the CDN cannot guarantee that residency.

## How

### How it works under the hood

The request lifecycle through a modern POP:

1. **Anycast routes** the user's TCP SYN to the closest POP (by BGP path), terminates TLS, and parses the HTTP request.
2. The cache **computes the key** — by default `(scheme, host, path)`, plus whatever the vendor's cache policy adds (sorted query params, selected headers, device class).
3. It performs a **lookup**. If present and *fresh* (`age < freshness_lifetime`), return immediately with `X-Cache: HIT`.
4. If present but *stale*, behavior depends on directives:
   - With `stale-while-revalidate=N`, serve the stale copy now and trigger a background revalidation ([RFC 5861 §3](https://www.rfc-editor.org/rfc/rfc5861.html#section-3)).
   - With `stale-if-error=N`, serve stale only if the revalidation fails.
   - Otherwise, do a conditional `If-None-Match` / `If-Modified-Since` request to origin.
5. If absent (MISS), the cache forwards to the next tier — a **regional shield** in tiered-cache setups, otherwise the origin. Concurrent misses for the same key are **coalesced**: only one fetch goes upstream, the rest block on it. Cloudflare, Fastly, CloudFront, and [Vercel](https://vercel.com/blog/cdn-request-collapsing) all implement this.
6. The origin response is parsed; the cache decides whether it is storable using `Cache-Control` (public vs private, `no-store`, `s-maxage`, presence of `Set-Cookie` heuristics — vendors differ here).
7. The object is written to disk/RAM, indexed by cache key, tagged with any `Surrogate-Key` headers, and the response is sent to the user.
8. Later, invalidation events arrive over a separate gossip/pub-sub channel and either delete the object (hard purge) or flip a flag making it stale (soft purge).

### Cache-key construction in detail

The default key is intentionally narrow because every dimension you add multiplies the cache footprint and shrinks the hit ratio. Production gotchas:

- **Query strings.** Tracking parameters (`utm_*`, `fbclid`, `gclid`) explode your key space if included. Most vendors offer an "ignore query string" or "include only these" mode. Cloudflare additionally offers [Query String Sort](https://developers.cloudflare.com/cache/advanced-configuration/query-string-sort/) so `?a=1&b=2` and `?b=2&a=1` collapse to one key.
- **Headers.** Including `User-Agent` directly is almost always a mistake (thousands of variants). Use a normalized class like `CF-Device-Type` (`mobile`/`tablet`/`desktop`) instead.
- **Cookies.** The classic foot-gun: `Vary: Cookie` combined with a per-user session ID gives you one cache entry per user, which is the same as no cache at all. Strip session cookies before lookup, or include only specific named cookies (e.g. `theme=dark`) in the key.
- **`Vary` header.** Tells the *downstream* cache to split on a request header. `Vary: Accept-Encoding` is essential (separate `gzip` vs `br` bodies); `Vary: *` is essentially "don't cache."

### TTL governance

Freshness is decided by precedence: `s-maxage` (shared caches only) > `max-age` > `Expires` > heuristic (typically 10% of `Last-Modified` age). Useful directives:

- `Cache-Control: public, max-age=60, s-maxage=3600` — browsers cache 1 min, CDN caches 1 hour. Standard pattern for HTML-with-personalization.
- `stale-while-revalidate=600` — keep serving for 10 min past TTL while a background fetch refreshes ([Fastly docs](https://www.fastly.com/documentation/guides/concepts/cache/stale/)).
- `stale-if-error=86400` — survive a 24-hour origin outage on cached paths.
- `immutable` ([RFC 8246](https://www.rfc-editor.org/rfc/rfc8246)) — for fingerprinted assets; tells browsers not to revalidate on reload.
- `Surrogate-Control` ([W3C Edge Arch](https://www.w3.org/TR/edge-arch)) — TTL the CDN sees but the browser ignores. Stripped at the edge.

Origin overrides ("page rules", "cache rules") can force TTLs regardless of what the origin sent — useful when you don't control the origin (S3, third-party API) but want longer edge retention.

### Invalidation

Three escalating mechanisms:

1. **URL purge** — invalidate one exact key. Cheap, surgical. Won't catch variants from `Vary` or query strings.
2. **Tag / surrogate-key purge** — the origin attaches `Surrogate-Key: product-42 category-shoes` on responses; later a single API call purges everything tagged `product-42`. Fastly pioneered this; see [Fastly's surrogate-key purging docs](https://www.fastly.com/documentation/guides/full-site-delivery/purging/purging-with-surrogate-keys/). Cloudflare's equivalent is Cache-Tag (Enterprise).
3. **Full purge / wildcard** — nuke everything. Use sparingly; it triggers a stampede on the next wave of traffic.

**Soft vs hard purge.** A hard purge deletes the object immediately; the next request is a guaranteed MISS. A soft purge marks the object stale, which means it remains eligible for `stale-while-revalidate` and `stale-if-error` ([Fastly soft purges](https://www.fastly.com/documentation/guides/full-site-delivery/purging/soft-purges/)). Prefer soft purge in production — it spreads origin load and tolerates origin downtime.

**Cache versioning via fingerprinted URLs.** The most reliable "invalidation" is not invalidating at all: ship assets as `/static/app.4f2a1b.js` and change the filename on every build. The old key simply ages out; the new key is a fresh miss followed by long-lived hits. This is the only safe way to combine `Cache-Control: max-age=31536000, immutable` with iterative development.

### Hierarchical / tiered caching

Without tiering, every POP that misses goes to origin directly — N POPs producing N origin fetches for the same cold object. Tiered caching inserts a smaller set of "upper-tier" POPs between edges and origin:

```
user -> edge POP (lower tier) -> regional shield -> upper tier -> origin
```

Cloudflare's [Smart Tiered Cache](https://developers.cloudflare.com/cache/how-to/tiered-cache/) auto-picks the upper tier closest to origin; [Regional Tiered Cache](https://developers.cloudflare.com/smart-shield/configuration/regional-tiered-cache/) adds a hub layer between. CloudFront calls its variant "Origin Shield." Bunny's [Perma-Cache](https://support.bunny.net/hc/en-us/articles/360017093479-Understanding-Perma-Cache) goes further: it replicates objects to permanent regional storage so the origin is contacted at most once. The trade-off is a small extra hop on miss for a major drop in origin RPS.

### Request coalescing (cache-fill stampedes)

When a popular object's TTL expires and 10,000 requests arrive simultaneously, naive caches send 10,000 fetches to origin. **Request coalescing** (also called "collapse forwarding" or "single-flight") holds N−1 of them at the edge and lets one fetch fill the cache; the rest are served from the now-fresh object. All major CDNs implement this per-POP; tiered caches extend the coalescing across POPs by funneling through a shared upper tier. The combination is what makes a 30-second TTL on a viral resource safe.

### Dynamic-content acceleration

- **ESI (Edge Side Includes)** — a [W3C-submitted markup](https://www.w3.org/TR/esi-lang) where the origin returns a page skeleton with `<esi:include src="/header"/>` tags. The edge fetches and assembles fragments, each with its own TTL. Lets you cache the 90% static frame at 1 hour while re-fetching the 10% personalized fragment every request. Supported by Akamai, Fastly, Varnish, LiteSpeed; not by Cloudflare's default cache.
- **Micro-caching** — set TTLs of 1–10 seconds on otherwise "uncacheable" pages. Under load, a 5-second TTL still collapses thousands of requests into one origin fetch; under no load, the staleness is invisible. Originally an nginx pattern for WordPress; now standard for news sites.
- **Edge functions doing custom cache logic** — Cloudflare Workers' Cache API and CloudFront Functions let you compute a personalized cache key (e.g. country + plan tier) and serve from cache for the slice of users that share it.

### Key trade-offs

| Choice | Gain | Cost |
|---|---|---|
| Long TTL | Higher hit ratio, lower origin RPS | Stale content windows, harder rollbacks |
| Include header/cookie in key | Per-variant correctness | Cache fragmentation, lower hit ratio |
| Surrogate-key purge | Surgical invalidation | Extra tagging discipline in origin code |
| Tiered cache | Fewer origin fetches | Extra ~10–40 ms on MISS |
| Soft purge + SWR | Origin-outage resilience | Users see stale data briefly |
| ESI | Cache the cacheable parts of dynamic pages | Origin must emit fragments, edge must parse them |
| Micro-caching | Cache "uncacheable" pages safely | Up to N-second staleness window |

### Common failure modes

- **Cache poisoning via unkeyed input** — origin reflects an unkeyed header (e.g. `X-Forwarded-Host`) into the response; one attacker request pollutes the entry for everyone. Fix: include reflected inputs in the key or strip them.
- **`Vary: Cookie` with session IDs** — collapses hit ratio to ~0%. Fix: strip session cookies before lookup, or use a custom cache key.
- **`Set-Cookie` on a cached response** — many CDNs refuse to cache responses with `Set-Cookie`; others cache it and leak the cookie to other users. Always strip on cacheable paths.
- **Stampede after full purge** — flushing everything causes a synchronized MISS storm. Fix: soft purge, or warm via crawl.
- **TTL drift between browser and edge** — `max-age` shared between both leads to browsers holding old assets long after edge purge. Fix: split via `s-maxage`, use fingerprinted URLs.
- **Heuristic caching of "no-cache-header" responses** — without `Cache-Control` or `Expires`, some caches assign a heuristic TTL and cache things you didn't intend. Fix: always send explicit headers.
- **Query-string explosion from tracking params** — UTM/fbclid create unique keys. Fix: strip or sort.

## Why

### Why it exists

Latency is bounded by physics (~1 ms per 100 km one-way over fiber, plus switching). A request from Sydney to Virginia is ~160 ms RTT minimum, and that is before TCP handshakes, TLS, and origin processing. Edge caching attacks this in two ways: it moves the *response* close to the user, eliminating most of that RTT, and it eliminates the origin from the critical path entirely for cacheable content. The same mechanism doubles as economic optimization (origin egress is expensive; CDN egress is cheap) and as resilience (origin can be down for hours while cached paths keep serving).

### Why it looks the way it does

The obvious alternative is **replicated origins** — full app servers and databases in every region. People build that (active-active multi-region) but it forces you to solve distributed consistency for *write* paths, which is expensive in engineering time and infrastructure. Edge caching deliberately punts on writes: it caches only idempotent GETs, accepts bounded staleness, and lets the origin remain a single source of truth. That asymmetry (cache reads, centralize writes) is why it scales so cheaply — caches don't need a consensus protocol. The design also leans on HTTP's existing cache semantics rather than inventing new ones, which is why most of the configuration you write is just `Cache-Control` headers your origin already understood.

### Why it matters now

In 2026, edge caching is table stakes — every major framework (Next.js, Remix, SvelteKit, Astro) emits `Cache-Control` headers automatically and ships docs assuming a CDN is in front. The interesting frontier has shifted: edge platforms now blur the line between cache and compute (Cloudflare Workers KV, Vercel Data Cache, Fastly Compute), and ML inference is being pushed to the edge for latency. Understanding the cache primitives precisely is what lets you reason about those higher-level products instead of treating them as magic.

## Open questions / things to verify in practice

- For your stack, what is the actual default TTL when origin sends no `Cache-Control`? Vendors differ — test with `curl -I` and watch `Age`.
- Does your CDN coalesce across POPs by default, or only within a single POP? This determines whether you need to enable tiered caching explicitly.
- How does your origin's framework set `Set-Cookie` on supposedly cacheable routes? One stray cookie can disable caching site-wide.
- What is your actual hit ratio per content type? If JS/CSS isn't >95%, your fingerprinting or TTLs are wrong.
- How long does a tag-based purge actually take to propagate globally? Vendors claim "<150 ms" but measure it under load.
- Do your `Vary` headers match the dimensions your origin actually varies on? Mismatch in either direction is a correctness bug.
