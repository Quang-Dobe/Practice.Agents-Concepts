# CDN Edge Caching — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In a typical SaaS product, the CDN is the layer you forget exists until something breaks. It sits in front of your marketing site, your SPA's static bundle, your image uploads, your unauthenticated API endpoints, and increasingly your server-rendered HTML. The default `Cache-Control` headers your framework emits will already do something — the question in production is whether they do the *right* thing.

In e-commerce, the CDN is load-bearing: product pages, category listings, and image variants live there, and a botched purge during a price change becomes a customer-support incident within minutes. In media and news, micro-caching turns a single-origin Rails or WordPress site into something that survives the front page of Hacker News. In B2B platforms with global users, the CDN is what keeps p99 latency from being dominated by transatlantic RTT to your one `us-east-1` deployment.

You will most often inherit a half-configured CDN — someone enabled Cloudflare three years ago, hit ratio is 42%, and nobody knows why. The skill being asked of you is to read the headers and the analytics, find the cache-killer, and fix it without breaking personalization.

## Best practices

### 1. Fingerprint your static assets and cache them forever
**Do:** Build pipelines (Vite, webpack, esbuild) should emit content-hashed filenames (`app.4f2a1b.js`). Serve those with `Cache-Control: public, max-age=31536000, immutable` ([RFC 8246](https://www.rfc-editor.org/rfc/rfc8246)).
**Why:** Invalidation is the hardest part of caching. Fingerprinting deletes the problem — old URLs age out, new URLs are fresh by definition. `immutable` further tells browsers to skip revalidation on reload, which removes a measurable burst of conditional requests on every page navigation.
**Avoid:** Serving `app.js` with a short TTL and purging on every deploy. You pay a global MISS storm every release.

### 2. Split browser TTL from edge TTL with `s-maxage`
**Do:** For HTML and JSON, use `Cache-Control: public, max-age=0, s-maxage=300, stale-while-revalidate=60`. Browsers always revalidate; the edge holds the response for 5 minutes.
**Why:** Browser caches you cannot purge. Edge caches you can. Keeping the user-side TTL low means a purge actually fixes the user's view; the edge still absorbs ~99% of origin load on hot paths.
**Avoid:** A single `max-age=3600` shared between both. After a purge, users still see stale content for up to an hour because their browser holds a copy you cannot reach.

### 3. Use surrogate keys / cache tags for granular invalidation
**Do:** Tag responses by domain entity. A product page emits `Surrogate-Key: product-42 category-shoes brand-acme`; a price update fires one purge for `product-42` ([Fastly surrogate keys](https://www.fastly.com/documentation/guides/full-site-delivery/purging/purging-with-surrogate-keys/)). Cloudflare Enterprise calls this `Cache-Tag`.
**Why:** Without tags, your only invalidation tool is "purge by URL" (you must enumerate every variant) or "purge everything" (you cause an origin stampede). Tags let one mutation invalidate exactly the cached objects it should and nothing else.
**Avoid:** Doing a wildcard purge after every CMS edit. It works for a small site and silently becomes a self-inflicted DDoS as you grow.

### 4. Prefer soft purge plus stale-while-revalidate
**Do:** When you purge, soft-purge (mark stale) rather than hard-purge (delete). Pair with `stale-while-revalidate=60` and `stale-if-error=86400` ([Fastly soft purges](https://www.fastly.com/documentation/guides/full-site-delivery/purging/soft-purges/)).
**Why:** A hard purge guarantees a MISS on the next request and N concurrent MISSes hit origin. Soft purge lets the edge serve stale for milliseconds while one fetch refills the entry. The `stale-if-error` clause means your site stays up through a multi-hour origin outage on any path that was warm.
**Avoid:** Hard-purging hot paths during traffic spikes. That is precisely when you cannot afford the origin RPS.

### 5. Enable origin shielding / tiered cache
**Do:** Turn on Cloudflare's [Smart Tiered Cache](https://developers.cloudflare.com/cache/how-to/tiered-cache/), CloudFront's Origin Shield, or Fastly's Shielding. Pick the shield POP closest to your origin region.
**Why:** Without it, every one of ~300 edge POPs that gets a MISS goes directly to origin. With it, MISSes funnel through a handful of upper-tier POPs, where request coalescing actually works globally instead of per-POP. Real-world origin RPS often drops 5–10x after enabling it.
**Avoid:** Skipping it because "we already have a CDN." A flat-topology CDN can still hammer your origin during a viral moment.

### 6. Strip cookies and tracking params from the cache key
**Do:** Configure your CDN to ignore `utm_*`, `fbclid`, `gclid`, and any analytics cookies on cacheable paths. Use Cloudflare's [Query String Sort](https://developers.cloudflare.com/cache/advanced-configuration/query-string-sort/) or "ignore query string" where order doesn't matter.
**Why:** Every distinct query string or cookie value is a separate cache entry. A single marketing campaign with five UTM variants multiplies your key space by five and slashes hit ratio. Session cookies in the key collapse hit ratio to near zero — one entry per user is the same as no cache.
**Avoid:** Letting the default "include full URL and all cookies" key persist. Audit what's actually in the key for every cached route.

### 7. Normalize `Vary` axes and never use `Vary: Cookie`
**Do:** Emit only `Vary: Accept-Encoding` and, if needed, a CDN-provided normalized header (`CF-Device-Type`, `CloudFront-Is-Mobile-Viewer`). Strip raw `User-Agent` and `Cookie` from any Vary.
**Why:** `Vary: User-Agent` produces thousands of variants — modern browsers fingerprint heavily. `Vary: Cookie` is the same disaster as keying on cookies. Either silently destroys your hit ratio and you'll spend a week finding it.
**Avoid:** Letting a backend framework auto-emit `Vary: Cookie` because it sets a session cookie. Strip the cookie on cacheable routes before the response leaves origin.

### 8. Cache by intent, not by extension
**Do:** Decide caching policy per route in code, with explicit `Cache-Control` headers, alongside a CDN rule that asserts the same thing. Static assets, public HTML, authenticated JSON, and personalized HTML each get a documented policy.
**Why:** "Cache everything that looks like an image" rules drift. A PDF generation endpoint that happens to end in `.pdf` should not be cached, but a path-suffix rule will cache it. Explicit headers from the application are auditable in code review.
**Avoid:** Configuring caching only in the dashboard. The next engineer has no idea the rule exists until something breaks.

### 9. Defend against cache poisoning from unkeyed inputs
**Do:** Identify every header your origin reflects into a response (`X-Forwarded-Host`, `X-Original-URL`, custom `X-*`). Either strip them at the edge before they reach origin, include them in the cache key, or never reflect them. See [PortSwigger's cache poisoning research](https://portswigger.net/web-security/web-cache-poisoning).
**Why:** An attacker who finds an unkeyed header that influences the response can poison one cache entry that then serves their payload to every user routed to that POP. This has happened to major sites and the blast radius is "everyone."
**Avoid:** Trusting `Host`-like headers to construct absolute URLs. If your origin emits `<link href="https://{Host}/style.css">`, someone will set `Host: evil.com` and poison the entry.

### 10. Personalize without breaking the cache
**Do:** Push personalization out of the document. Render the shell at the edge, hydrate per-user data with a separate uncached fetch, or use Edge Side Includes ([ESI](https://www.w3.org/TR/esi-lang)) / edge functions to assemble fragments with independent TTLs. For binary splits (logged-in vs anonymous, country, plan tier), compute a coarse cache-key variant — not one per user.
**Why:** Personalization is the most common reason teams declare a route "uncacheable." Almost always, the cacheable 90% can be separated from the personalized 10%. Caching the shell turns p95 from 800 ms to 40 ms even when the user-specific bit still hits origin.
**Avoid:** Stuffing the username into the HTML and concluding the page can't be cached. The page can be cached; the username can be injected client-side or via an edge worker.

### 11. Monitor hit ratio per surface, not site-wide
**Do:** Track hit ratio bucketed by content type and route (`/_next/static/*`, `/api/products/*`, `/`). Alert when a specific bucket regresses, not when the global number wobbles. Watch origin egress, cache-key cardinality, and purge-API latency.
**Why:** A site-wide 85% hit ratio can hide a freshly-deployed route at 4%. Cardinality silently explodes when someone adds a new query param to product links; you only notice when the next region's POPs evict everything else to make room.
**Avoid:** Watching the single "Cache Hit Ratio" number on the vendor dashboard. It averages your wins with your losses.

## Anti-patterns to recognize

- **`no-cache` as a panic button.** Slapping `Cache-Control: no-cache, no-store` on every endpoint because one bug leaked private data. You now serve everything from origin and your latency and bill both double. Fix the specific route, don't disable the layer.
- **Purge-on-every-deploy.** CI runs a global purge after each release. Works at 100 RPS, causes a coordinated MISS storm at 10k RPS. Fingerprint assets and tag dynamic content instead.
- **Cookies in the cache key by default.** Vendor or framework defaults to "include all cookies." Hit ratio is mysteriously 30%. Strip session cookies on cacheable routes; whitelist only the cookies that actually change rendering.
- **Query-string sprawl.** Marketing appends `?utm_source=...` to every shared link, the CDN keys on full query, and the same article exists as 50 cache entries. Strip tracking params at the edge.
- **`Set-Cookie` leaking through cacheable responses.** A login handler accidentally emits `Set-Cookie` on a cached path; some CDNs cache and serve that cookie to other users — a session-hijack vector. Strip `Set-Cookie` on cacheable responses, hard.
- **Trusting heuristic caching.** Origin sends no `Cache-Control`; the CDN applies a heuristic TTL (often 10% of `Last-Modified` age) and caches a thing you never meant to cache. Always send explicit headers from origin.
- **Treating the CDN as a free database.** Storing API responses with 24-hour TTLs and using purges as writes. Works until purge latency, eventual consistency, and key cardinality bite simultaneously. If you need a key-value store, use one.
- **Web cache deception.** An attacker requests `/profile/account.css` — the framework returns the user's profile, the CDN sees a `.css` extension and caches it, the attacker fetches it from another session ([HackTricks summary](https://hacktricks.wiki/en/pentesting-web/cache-deception/index.html)). Match cache rules to content-type, not URL suffix.

## Real-world usage patterns

**News site under a traffic spike.** A regional newspaper sees a story trend nationally. Origin is a single Rails monolith. They run Fastly with micro-caching at 10-second TTL on article HTML, surrogate keys per article ID, and `stale-while-revalidate=60`. A traffic spike from 200 to 80k RPS lands on origin as ~6 RPS thanks to coalescing and tiered cache. Lesson: even an "uncacheable" CMS page is cacheable for 10 seconds, and 10 seconds is all you need under load.

**E-commerce product catalog.** A mid-size retailer tags product pages with `Surrogate-Key: product-{id} category-{slug}`. Inventory updates fire one tag purge per affected SKU. Soft purge plus `stale-if-error=3600` means a midnight origin deploy doesn't take the store down. Lesson: invalidation discipline lives in the origin code — the `Surrogate-Key` header has to be emitted correctly on every cacheable response, which means it belongs in a single response-helper, not scattered across controllers.

**SaaS dashboard with global users.** Authenticated dashboard, mostly uncacheable. Team splits the shell HTML (cached at the edge, 5-minute TTL, anonymous variant only) from `/api/me` (never cached). Edge worker injects the user's display name client-side. P95 page load drops from 1.2 s to 180 ms in APAC without changing the backend. Lesson: most dashboards have a cacheable shell; you just have to be willing to refactor the page to find it.

**Public API with API keys.** A developer API serves the same data to many clients with per-key rate limits. Cache key includes only the path and sorted query; the API-key header is unkeyed and used only for auth/rate-limit at the edge. Hit ratio above 95%, origin sees ~3% of public traffic. Lesson: authentication does not have to be in the cache key — separate "may this client see it" from "what is the content."

**Video segment delivery.** A streaming service serves HLS segments (`segment-00042.ts`) with year-long TTLs and fingerprinted manifests. Origin shielding via CloudFront Origin Shield reduces origin egress by ~95%. Lesson: file naming is your invalidation strategy; the CDN is just a cache that happens to be huge.

## Operational checklist

- **Hit ratio per route bucket** is monitored, with alerts on regressions per bucket (not just the global average).
- **Cache-key cardinality** is tracked or audited; new query params and headers in the key trigger a review.
- **Origin egress and origin RPS** are graphed alongside CDN traffic — a divergence is the first signal of a cache-killer deploy.
- **Purge propagation latency** has been measured under load, not just from the vendor's marketing page.
- **`Set-Cookie` on cacheable paths** is impossible by construction (middleware strips it) or asserted in tests.
- **Reflected headers** (`Host`, `X-Forwarded-*`, custom) are either stripped at the edge or included in the cache key — write down which.
- **Stale-if-error** is configured on critical paths; a contrived origin outage in staging confirms the site still serves.
- **Day-one onboarding doc** explains: how to set cache headers, how to issue a purge, who owns surrogate-key tagging, and the one Slack channel that pages when hit ratio drops.
- **Cost model:** the team knows the per-GB egress price, the purge API rate limits, and which features (Cache Reserve, persistent storage) add a line item.
- **Security review** has confirmed no cacheable route returns user-private data (test by requesting the same URL with two sessions and diffing).

## How this topic typically evolves in a codebase

Most projects start by enabling a CDN in front of static assets and calling it done. Hit ratio is fine because the framework already fingerprints JS/CSS. The first real decision arrives when someone wants to cache HTML — usually because TTFB in a far region is embarrassing. That is when the team meets `Vary`, session cookies, and the first cache-key foot-gun.

The painful migration point is the move from URL purges to tag-based purges. Teams put it off because tagging requires touching every controller. They eventually do it the day a routine CMS edit triggers a 30-second origin overload, or the day a security review flags that a stale price was shown after an inventory update. Surrogate keys retroactively force a design decision: which entities own which cached views? That mapping is hard to add later, easy to bake in early.

The endgame, for teams operating at scale, is to treat the edge as a programmable layer — edge workers compute custom cache keys, ESI assembles personalized pages from cached fragments, and the "origin" is increasingly a write path plus a cold-storage backup. The instinct shifts from "what can I cache?" to "what specifically can I *not* cache, and why?"

## Further reading

- [RFC 9111: HTTP Caching](https://www.rfc-editor.org/rfc/rfc9111) — the spec. Read at least sections 4 (constructing responses from caches) and 5 (header definitions); everything vendors do is a flavor of this.
- [PortSwigger: Web Cache Poisoning](https://portswigger.net/web-security/web-cache-poisoning) — James Kettle's research. Will make you paranoid in a productive way.
- [Fastly: Working with surrogate keys](https://www.fastly.com/documentation/guides/full-site-delivery/purging/working-with-surrogate-keys/) — the canonical pattern for granular invalidation, even if you're not on Fastly.
- [Cloudflare: Tiered Cache](https://developers.cloudflare.com/cache/how-to/tiered-cache/) — readable explanation of origin shielding mechanics that applies to every CDN.
- [Fastly blog: stale-while-revalidate](https://www.fastly.com/documentation/guides/concepts/cache/stale/) — the directive that quietly does most of the resilience work in a production cache setup.
- [Google Web Fundamentals: HTTP caching](https://web.dev/articles/http-cache) — Jake Archibald's pragmatic guide. The "two patterns" framing (immutable fingerprinted vs always-revalidated) is the right mental model for most teams.
