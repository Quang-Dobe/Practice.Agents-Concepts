# CDN Edge Caching — MVP Code

A tiny "origin" and a tiny "edge" running in the same Node process, driven by a scripted client. About 130 lines of actual code, comments excluded.

## What it demonstrates

- **Cache key construction** — method + path + sorted query string. `?a=1&b=2` and `?b=2&a=1` collapse to one entry.
- **`Cache-Control` honoring** — `max-age` controls freshness, `no-store` bypasses, `stale-while-revalidate` serves stale then refreshes in the background.
- **HIT / MISS / STALE** — surfaced via an `X-Cache` response header, the same debug convention real CDNs use.
- **Request coalescing** — 50 concurrent requests for one cold key produce **one** origin fetch.

## Prerequisites

- Node.js 20+
- One dev dependency: `tsx` for running TypeScript directly.

```bash
npm install --no-save tsx
```

## Run it

```bash
npx tsx mvp.ts
```

## Expected output

Seven labeled sections walk through MISS, HIT, sorted-query equivalence, `no-store` bypass, a 50-way coalesced burst hitting origin exactly once, TTL expiry, and stale-while-revalidate. The final line reports total origin fetches — around 8, despite ~60 client requests.

## What to try next

- Set `max-age=0` on `/news` — every request becomes a MISS.
- Remove the `inflight` lookup in `fetchCoalesced` and re-run section 5 — origin gets hit 50 times.
- Add `Cookie` to the cache key in `cacheKeyOf` and watch hit ratio collapse.
