/**
 * sw.ts — the Service Worker source.
 *
 * What this file proves, end to end:
 *   1. `install`  — pre-populate a versioned cache with the app shell.
 *   2. `activate` — delete any cache whose name is not in our allow-list.
 *   3. `fetch`    — intercept every in-scope request and answer it with a
 *                   cache-first strategy (textbook example from 02-deep-dive.md).
 *
 * Read top to bottom. Three event handlers, no abstractions.
 */

// A Service Worker script's global is `ServiceWorkerGlobalScope`, not `Window`.
// This line re-types `self` so the rest of the file gets `caches`, `clients`,
// `skipWaiting`, `registration`, etc. with full IntelliSense and strict checks.
declare const self: ServiceWorkerGlobalScope;

// Versioned cache name. Bumping the suffix (v1 -> v2) is the entire migration
// story: the next `activate` will sweep away the previous version. See the
// "Cache versioning" pattern in 02-deep-dive.md § How.
const CACHE_NAME = 'app-shell-v1';

// The "app shell" — the minimum set of URLs that must be in the cache for the
// page to render without network. Hard-coded on purpose: a manifest of hashed
// asset URLs is what Workbox generates at build time; this is the from-scratch
// version. `as const` keeps the array readonly and narrow.
const SHELL_URLS = [
  '/',
  '/index.html',
  '/style.css',
] as const;

// ---- install -------------------------------------------------------------
// Fires once per worker version. `event.waitUntil(promise)` extends the
// `installing` state until the promise settles — without it, the worker would
// transition to `installed` before `cache.addAll` finishes, leaving a
// half-populated cache (one of the anti-patterns in 03-practice.md).
self.addEventListener('install', (event) => {
  event.waitUntil(
    (async () => {
      const cache = await caches.open(CACHE_NAME);
      // `addAll` is all-or-nothing: if any request fails (404, network error,
      // CORS), the whole install rejects and the worker becomes `redundant`.
      // That is the correct behavior — we never want a half-installed shell.
      await cache.addAll(SHELL_URLS);
    })(),
  );
});

// ---- activate ------------------------------------------------------------
// Fires when this worker is about to take control. The right place to clean
// up old caches: at this point the new worker has fully installed, so old
// tabs still controlled by the previous worker can keep reading their cache
// right up until they navigate or close.
self.addEventListener('activate', (event) => {
  event.waitUntil(
    (async () => {
      const keys = await caches.keys();
      await Promise.all(
        keys
          .filter((key) => key !== CACHE_NAME)
          .map((key) => caches.delete(key)),
      );
    })(),
  );
});

// ---- fetch ---------------------------------------------------------------
// Fires for every request whose URL is in scope, including the HTML
// navigation, every subresource, and every explicit `fetch()` call from JS.
self.addEventListener('fetch', (event) => {
  // `respondWith` MUST be called synchronously, before the microtask queue
  // drains (see 03-practice.md). Do all async work *inside* the promise we
  // pass, never `await` before this line.
  event.respondWith(cacheFirst(event.request));
});

/**
 * Cache-first with network fallback — the textbook strategy for immutable,
 * fingerprinted assets and any "shell" resource.
 *
 * Shape of an alternative:
 *   - Network-first  → try `fetch(request)` with a timeout, fall back to
 *                      `cache.match(request)` on rejection. Used for HTML and
 *                      "freshness matters" APIs.
 *   - Stale-while-revalidate → return `cache.match(request)` immediately,
 *                      kick off `fetch(request)` in the background inside
 *                      `event.waitUntil`, write the result to the cache for
 *                      next time. Used for avatars, lists, anything tolerant
 *                      of being a few seconds stale.
 *
 * Swap the body of this function and you've changed strategies — that is the
 * whole point of writing one from scratch instead of pulling in Workbox.
 */
const cacheFirst = async (request: Request): Promise<Response> => {
  const cache = await caches.open(CACHE_NAME);

  // Step 1: ask the cache. `match` returns `undefined` on miss.
  const cached = await cache.match(request);
  if (cached) {
    // Echo a header so the page can prove the worker handled it. Have to
    // build a new Response because Response headers are immutable.
    const headers = new Headers(cached.headers);
    headers.set('x-served-from', 'service-worker-cache');
    return new Response(cached.body, {
      status: cached.status,
      statusText: cached.statusText,
      headers,
    });
  }

  // Step 2: cache miss — go to the network.
  const response = await fetch(request);

  // Step 3: opportunistically populate the cache for next time. Only cache
  // successful, non-opaque responses; opaque (cross-origin no-CORS) responses
  // can fill quota at a padded size and can't be inspected later. GETs only
  // because the Cache API rejects other methods.
  if (response.ok && response.type !== 'opaque' && request.method === 'GET') {
    // Bodies are streams that can only be consumed once. Clone before storing
    // or the page receives an already-drained Response.
    cache.put(request, response.clone());
  }

  return response;
};
