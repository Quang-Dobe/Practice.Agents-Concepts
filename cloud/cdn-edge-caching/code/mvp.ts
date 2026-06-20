/**
 * CDN Edge Caching — MVP
 *
 * A tiny "origin" and a tiny "edge" running in the same Node process,
 * driven by a scripted client. The point is to make the abstract pieces
 * from the deep-dive doc tangible:
 *
 *   - cache key construction (method + path + sorted query)
 *   - Cache-Control honoring (max-age, no-store)
 *   - HIT vs MISS, surfaced via the X-Cache header
 *   - TTL expiry triggering a re-fetch
 *   - request coalescing (single-flight) on concurrent misses
 *   - stale-while-revalidate: serve stale instantly, refresh in the background
 *
 * Run:  npx tsx mvp.ts
 *
 * No external deps. Pure Node stdlib + TypeScript.
 */

import { createServer, request, type IncomingMessage, type ServerResponse } from 'node:http';
import { setTimeout as sleep } from 'node:timers/promises';

const ORIGIN_PORT = 8081;
const EDGE_PORT = 8080;

// ---------------------------------------------------------------------------
// ORIGIN
// ---------------------------------------------------------------------------
// A deliberately slow backend. It counts the requests it actually serves so
// we can prove caching works ("origin saw N requests, edge served M").
// It returns different Cache-Control headers per route so the edge can
// demonstrate honoring them.

let originHits = 0;

const originHandler = async (req: IncomingMessage, res: ServerResponse) => {
  originHits += 1;
  // 200 ms latency makes HIT vs MISS visible to the naked eye in the output.
  await sleep(200);

  const url = req.url ?? '/';
  // Route /static/*  -> long TTL, immutable-ish.
  // Route /news      -> short TTL, with stale-while-revalidate.
  // Route /private   -> not cacheable at all.
  // Anything else    -> max-age=2 so TTL expiry is observable in seconds.
  const headers: Record<string, string> = { 'Content-Type': 'text/plain' };
  if (url.startsWith('/static/')) {
    headers['Cache-Control'] = 'public, max-age=60';
  } else if (url.startsWith('/news')) {
    headers['Cache-Control'] = 'public, max-age=2, stale-while-revalidate=10';
  } else if (url.startsWith('/private')) {
    headers['Cache-Control'] = 'no-store';
  } else {
    headers['Cache-Control'] = 'public, max-age=2';
  }

  res.writeHead(200, headers);
  // Body changes every origin hit so we can tell a fresh fetch from a stale serve.
  res.end(`origin#${originHits} for ${url} @ ${new Date().toISOString()}`);
};

// ---------------------------------------------------------------------------
// EDGE (the actual CDN-ish part)
// ---------------------------------------------------------------------------

type CacheEntry = {
  body: Buffer;
  headers: Record<string, string>;
  storedAt: number;   // epoch ms when we wrote it
  maxAgeMs: number;   // freshness lifetime
  swrMs: number;      // stale-while-revalidate window past freshness
};

const cache = new Map<string, CacheEntry>();
// In-flight fetches keyed by cache key -> the Promise other concurrent
// requests await. This is "request coalescing" / "single-flight" /
// "collapse forwarding". One key = one outstanding origin fetch, ever.
const inflight = new Map<string, Promise<CacheEntry>>();

// Cache key: METHOD + path + sorted query. We deliberately do NOT include
// cookies, User-Agent, or unsorted query order — those are the classic
// cache-killers from the deep-dive doc.
const cacheKeyOf = (req: IncomingMessage): string => {
  const url = new URL(req.url ?? '/', `http://x`);
  const sortedQs = [...url.searchParams.entries()]
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([k, v]) => `${k}=${v}`)
    .join('&');
  return `${req.method ?? 'GET'} ${url.pathname}?${sortedQs}`;
};

// Parse just the directives we care about. A real CDN parses all of RFC 9111.
const parseCacheControl = (h: string | undefined): { maxAgeMs: number; swrMs: number; noStore: boolean } => {
  const out = { maxAgeMs: 0, swrMs: 0, noStore: false };
  if (!h) return out;
  for (const part of h.split(',').map((s) => s.trim().toLowerCase())) {
    if (part === 'no-store' || part === 'private') out.noStore = true;
    const m = part.match(/^(max-age|stale-while-revalidate)=(\d+)$/);
    if (m) {
      const seconds = Number(m[2]);
      if (m[1] === 'max-age') out.maxAgeMs = seconds * 1000;
      else out.swrMs = seconds * 1000;
    }
  }
  return out;
};

// One outbound fetch to origin. Returns a CacheEntry built from the response.
const fetchFromOrigin = (key: string, url: string): Promise<CacheEntry> => {
  return new Promise((resolve, reject) => {
    const r = request({ hostname: '127.0.0.1', port: ORIGIN_PORT, path: url, method: 'GET' }, (res) => {
      const chunks: Buffer[] = [];
      res.on('data', (c: Buffer) => chunks.push(c));
      res.on('end', () => {
        const body = Buffer.concat(chunks);
        const cc = parseCacheControl(res.headers['cache-control'] as string | undefined);
        const headers: Record<string, string> = {};
        for (const [k, v] of Object.entries(res.headers)) if (typeof v === 'string') headers[k] = v;
        const entry: CacheEntry = {
          body,
          headers,
          storedAt: Date.now(),
          maxAgeMs: cc.maxAgeMs,
          swrMs: cc.swrMs,
        };
        // Honor no-store: do not persist. Other requests get nothing from cache.
        if (!cc.noStore && cc.maxAgeMs > 0) cache.set(key, entry);
        resolve(entry);
      });
    });
    r.on('error', reject);
    r.end();
  });
};

// Coalesce: if a fetch for this key is already running, await it instead of
// starting another one. This is what stops a 10k-RPS spike from becoming
// 10k origin requests when an object goes cold.
const fetchCoalesced = (key: string, url: string): Promise<CacheEntry> => {
  const existing = inflight.get(key);
  if (existing) return existing;
  const p = fetchFromOrigin(key, url).finally(() => inflight.delete(key));
  inflight.set(key, p);
  return p;
};

const edgeHandler = async (req: IncomingMessage, res: ServerResponse) => {
  const key = cacheKeyOf(req);
  const url = req.url ?? '/';
  const hit = cache.get(key);
  const now = Date.now();

  // FRESH HIT: within max-age, serve from cache, do not touch origin.
  if (hit && now - hit.storedAt < hit.maxAgeMs) {
    res.writeHead(200, { ...hit.headers, 'X-Cache': 'HIT', 'Age': String(Math.floor((now - hit.storedAt) / 1000)) });
    res.end(hit.body);
    return;
  }

  // STALE-WHILE-REVALIDATE: past max-age but inside the SWR window.
  // Serve stale immediately, kick off a background refresh.
  if (hit && now - hit.storedAt < hit.maxAgeMs + hit.swrMs) {
    res.writeHead(200, { ...hit.headers, 'X-Cache': 'STALE', 'Age': String(Math.floor((now - hit.storedAt) / 1000)) });
    res.end(hit.body);
    // Background refresh — also coalesced so a burst of stale serves only
    // produces one origin fetch.
    fetchCoalesced(key, url).catch(() => { /* swallow; the cache stays stale */ });
    return;
  }

  // MISS: no entry, or fully expired past the SWR window. Fetch (coalesced).
  const fresh = await fetchCoalesced(key, url);
  res.writeHead(200, { ...fresh.headers, 'X-Cache': 'MISS', 'Age': '0' });
  res.end(fresh.body);
};

// ---------------------------------------------------------------------------
// CLIENT — scripted demo against the edge
// ---------------------------------------------------------------------------

const get = (path: string): Promise<{ status: number; xCache: string; age: string; body: string }> => {
  return new Promise((resolve, reject) => {
    const r = request({ hostname: '127.0.0.1', port: EDGE_PORT, path, method: 'GET' }, (res) => {
      const chunks: Buffer[] = [];
      res.on('data', (c: Buffer) => chunks.push(c));
      res.on('end', () =>
        resolve({
          status: res.statusCode ?? 0,
          xCache: (res.headers['x-cache'] as string) ?? '-',
          age: (res.headers['age'] as string) ?? '-',
          body: Buffer.concat(chunks).toString(),
        }),
      );
    });
    r.on('error', reject);
    r.end();
  });
};

const log = (label: string, r: { xCache: string; age: string; body: string }) => {
  console.log(`  [${r.xCache.padEnd(5)} age=${r.age.padStart(2)}s]  ${label.padEnd(38)} ${r.body}`);
};

const main = async () => {
  const origin = createServer(originHandler).listen(ORIGIN_PORT);
  const edge = createServer(edgeHandler).listen(EDGE_PORT);
  await sleep(50); // let listeners bind

  console.log('\n=== 1. First request is a MISS (origin pays the 200 ms) ===');
  log('GET /static/logo.png', await get('/static/logo.png'));

  console.log('\n=== 2. Repeat is a HIT (no origin contact, instant) ===');
  log('GET /static/logo.png', await get('/static/logo.png'));
  log('GET /static/logo.png', await get('/static/logo.png'));

  console.log('\n=== 3. Sorted-query cache key: ?a=1&b=2 == ?b=2&a=1 ===');
  log('GET /search?a=1&b=2 (MISS)', await get('/search?a=1&b=2'));
  log('GET /search?b=2&a=1 (HIT, same key)', await get('/search?b=2&a=1'));

  console.log('\n=== 4. no-store is honored: every request hits origin ===');
  log('GET /private/me', await get('/private/me'));
  log('GET /private/me', await get('/private/me'));

  console.log('\n=== 5. Request coalescing: 50 concurrent MISSes => 1 origin fetch ===');
  const before = originHits;
  const burst = await Promise.all(Array.from({ length: 50 }, () => get('/hot-launch')));
  const after = originHits;
  console.log(`  50 concurrent requests, origin saw ${after - before} fetch(es). X-Cache distribution:`,
    burst.reduce<Record<string, number>>((acc, r) => { acc[r.xCache] = (acc[r.xCache] ?? 0) + 1; return acc; }, {}));

  console.log('\n=== 6. TTL expiry: wait past max-age=2, next request is MISS again ===');
  await sleep(2100);
  log('GET /hot-launch (after 2.1s)', await get('/hot-launch'));

  console.log('\n=== 7. stale-while-revalidate: stale served instantly, refresh in background ===');
  log('GET /news (MISS, fills cache)', await get('/news'));
  await sleep(2100); // past max-age=2, inside swr=10
  const stale = await get('/news');
  log('GET /news (STALE, served fast)', stale);
  await sleep(300); // give the background refresh time to land
  log('GET /news (HIT, refreshed body)', await get('/news'));

  console.log(`\nOrigin handled ${originHits} request(s) total. The edge served the rest.\n`);

  origin.close();
  edge.close();
};

main().catch((e: unknown) => {
  console.error(e);
  process.exit(1);
});
