# Service Worker — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition
A Service Worker is an event-driven [W3C-standardized](https://www.w3.org/TR/service-workers/) script that the user agent runs in a worker context (`ServiceWorkerGlobalScope`), separate from any document, scoped to an origin and a URL prefix. It can intercept HTTP requests issued by clients in its scope and respond on their behalf. The spec models it as a state machine attached to a `ServiceWorkerRegistration` record stored per-origin in the browser's storage.

### The runtime environment
The global object is a `ServiceWorkerGlobalScope`, not `Window`. There is no DOM, no `document`, no `window`, no synchronous `XMLHttpRequest`, no `localStorage`. What you do get:

- `self` — the global itself.
- `self.registration` — the `ServiceWorkerRegistration` (lets you call `update()`, `unregister()`, `showNotification()`).
- `self.clients` — the `Clients` interface, used to enumerate and message controlled pages.
- `self.caches` — the `CacheStorage` (the entry point of the Cache API; same object that pages see).
- `self.skipWaiting()` — promotes a `waiting` worker straight into `activating`.
- `fetch`, `Request`, `Response`, `Headers`, `URL`, `crypto`, `indexedDB`, `postMessage`, `importScripts`.

State held in the global's variables is **not durable**. The browser is allowed to terminate the worker between events; on the next event it spins up a fresh instance and your variables are gone. Persistent state has to go to `caches` or `indexedDB`.

### Lifecycle state machine
A registration owns up to three slots: `installing`, `waiting`, `active`. Each slot holds at most one `ServiceWorker` object, and every `ServiceWorker` object exposes a `state` field that walks through this ordering:

```
parsed -> installing -> installed (waiting) -> activating -> activated -> redundant
```

The transitions:

1. **`parsed`** — `register()` was called, the script downloaded and parsed, origin is same-origin and HTTPS. No event has fired yet.
2. **`installing`** — the `install` event has been dispatched. The handler may call `event.waitUntil(promise)` to extend this phase; the worker stays in `installing` until that promise settles.
3. **`installed` / `waiting`** — `install` succeeded. If another worker is already `activated` for this registration, the new one parks here until every controlled client either closes or navigates away, unless `skipWaiting()` is called.
4. **`activating`** — the `activate` event is dispatched. Again, `event.waitUntil(promise)` extends this phase. The worker does **not** receive `fetch` events yet.
5. **`activated`** — the worker is now eligible to handle functional events (`fetch`, `message`, `push`, `sync`, `periodicsync`, `notificationclick`).
6. **`redundant`** — install failed, or a newer worker replaced this one, or `unregister()` was called. The object is dead.

If `install` rejects, the worker goes directly to `redundant`. If `activate` rejects, the worker is still considered activated by the spec — `activate` is a "you've already won, here's a chance to clean up" event, not a gate.

### How it relates to the broader landscape
Service Workers belong to the family of **Web Workers** (`DedicatedWorker`, `SharedWorker`, `ServiceWorker`), all of which run JavaScript off the main thread. Among them only Service Workers persist beyond the lifetime of any page and can intercept network traffic. The cross-platform analog is the **Cloudflare Workers / Deno Deploy / Vercel Edge** style of edge worker — same `fetch` event programming model, same `Request`/`Response` objects, but running on the server side. The deprecated predecessor on the browser side was **Application Cache (AppCache)**, a declarative manifest format removed from browsers around 2021.

## Where

### Where it sits in the stack
Architecturally, a Service Worker is a programmable HTTP proxy that lives on the client, between the page's `fetch`/`XMLHttpRequest`/resource-loading code and the network stack. In modern Chromium it runs out-of-process in a dedicated **service worker process**, with its own JS event loop, isolated from any renderer. Firefox and Safari run it on a separate worker thread; the isolation guarantee is the same — no shared memory with any page, communication is message-passing only.

### Where the script lives
The script is fetched from a URL on the same origin as the page that registered it. The directory of that URL caps the scope: `/sw.js` can claim any scope up to `/`; `/static/sw.js` can only claim scopes under `/static/`. To break out of that, the server must return the script with a [`Service-Worker-Allowed`](https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/Service-Worker-Allowed) response header listing a broader path. This is why every static-hosting tutorial insists the worker live at the site root.

### Security model
HTTPS-only, with `localhost` and `127.0.0.1` exempted for development. Same-origin everything: the script URL, the page that registers it, and the URLs it can claim. The browser may terminate it at any moment. Browsers also bypass the HTTP cache when fetching the worker script if the previous fetch was older than 24 hours, and silently clamp `max-age` on the script to 86400 seconds — this exists specifically to prevent a buggy worker from pinning itself forever (see [Chrome's "Fresher service workers" notes](https://developer.chrome.com/blog/fresher-sw)).

### Where you encounter it in the wild
- **Workbox** — Google's library that compiles caching strategies into a worker. Default tool for most production PWAs.
- **Next.js + `next-pwa`**, **Nuxt PWA module**, **Vite PWA plugin** — framework integrations, all wrappers around Workbox.
- **Web push** end-to-end (FCM, OneSignal) — the `push` event handler must live in a Service Worker.
- **Mock Service Worker (MSW)** — uses a Service Worker as a request interceptor for browser-side test mocking.
- **Google Docs, Twitter/X, YouTube Music, Pinterest** — all ship Service Workers for offline shells and asset caching.

## When

### When each lifecycle event fires
- `install` fires once per worker version, immediately after the script is parsed and the registration accepts it.
- `activate` fires once, when this worker moves out of `waiting` — either because all controlled clients are gone, or because `skipWaiting()` was called.
- `fetch` fires for every request whose URL is inside scope and that originates from a controlled client, **including** the page's HTML navigation request, plus all subresources and explicit `fetch()` calls.
- `message` fires when a client calls `worker.postMessage(...)`.
- `push` fires when the push service delivers a message; the worker is woken up specifically to handle it.
- `sync` and `periodicsync` fire when the browser decides to run a previously-registered background task — typically when connectivity returns or on a periodic budget.
- `notificationclick` / `notificationclose` fire from user interaction with a notification this worker posted.

### When updates happen
On every page navigation in scope, and after roughly 24 hours of idle, the browser refetches the worker script and does a **byte-for-byte comparison** with the stored copy. Since Chrome 78, the comparison includes any scripts pulled in via `importScripts()` — change one of those and the parent worker is treated as updated even if its own bytes are identical (see the [w3c/ServiceWorker PR #1023](https://github.com/w3c/ServiceWorker/pull/1023)). The HTTP cache is consulted for the script per [`updateViaCache`](https://developer.mozilla.org/en-US/docs/Web/API/ServiceWorkerRegistration/updateViaCache), which defaults to `imports` (cache imports, always revalidate the top-level script).

If any byte differs, a new worker enters `installing` while the old one keeps serving traffic.

### When `skipWaiting()` and `clients.claim()` take effect
- `skipWaiting()` only matters while the new worker is in `installing` or `waiting`. Called from `install`, it lets the new worker activate as soon as install finishes, ignoring whether old clients are still open.
- `clients.claim()` is called from the `activate` handler (or later). It tells the browser to set `controller` on every same-origin client in scope that currently has no controller, or that was being controlled by the previous worker. Without it, **clients keep their original controller until they reload** — so the first page load after registration is always uncontrolled.

### When the browser kills the worker
After about 30 seconds with no events (the figure cited in Chromium's extension docs; web worker timing is similar but unspecified), under memory pressure, or if a single event handler runs longer than the user agent allows (Chromium uses a 5-minute hard ceiling). It will spin back up on the next event.

## How

### Anatomy of a `fetch` interception
```js
self.addEventListener('fetch', (event) => {
  event.respondWith(handle(event.request));
});

async function handle(request) {
  const cache = await caches.open('static-v3');
  const cached = await cache.match(request);
  if (cached) return cached;
  const response = await fetch(request);
  if (response.ok) cache.put(request, response.clone());
  return response;
}
```

`event.respondWith(promiseOrResponse)` is the only way to take over a request. If you don't call it, the request passes through unchanged. The promise must resolve to a `Response`; if it rejects or the response is malformed, the page sees a network error. Bodies are streams and can only be consumed once — hence `response.clone()` before storing.

### The named caching strategies
- **Cache-first** — look in cache, fall back to network. Best for immutable, fingerprinted assets (`app.8a1f.js`).
- **Network-first** — try network with a timeout, fall back to cache. Best for HTML and API responses that change often but must work offline.
- **Stale-while-revalidate** — return cache immediately, kick off a network refresh in the background, update the cache for next time. Best for content that's allowed to be slightly stale (avatars, lists).
- **Cache-only** — return cache or fail. Used for precached app-shell assets.
- **Network-only** — never touch the cache. Used for POSTs, analytics, anything mutating.

These are not built-in primitives — they are conventions. Workbox ships them as classes; you can write them in ~10 lines each.

### Cache versioning, the working pattern
```js
const CACHE = 'app-shell-v7';

self.addEventListener('install', (event) => {
  event.waitUntil(caches.open(CACHE).then((c) => c.addAll(SHELL_URLS)));
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys().then((keys) =>
      Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k)))
    )
  );
});
```
Bumping the cache name (`v7` → `v8`) is the entire migration story. Old caches are deleted in `activate` *after* the new worker has fully installed, so old tabs keep working.

### Messaging clients
```js
const clients = await self.clients.matchAll({ includeUncontrolled: true });
clients.forEach((c) => c.postMessage({ type: 'cache-updated' }));
```
Direction-symmetric: pages send via `navigator.serviceWorker.controller.postMessage(...)`, the worker receives a `message` event with `event.source` pointing back at the `Client`.

### Registration
```js
// in the page
const reg = await navigator.serviceWorker.register('/sw.js', { scope: '/' });
// to remove
await reg.unregister();
```

### Trade-offs at a glance
| Choice | Gained | Given up |
|---|---|---|
| Run in a separate thread, no DOM | No main-thread blocking, no race conditions on `document` | Can't read DOM, must message-pass |
| HTTPS-only | Worker can't be MITM'd into existence | No plain-HTTP dev, no IP-served LAN demos |
| Staged install / activate | Old tabs keep working during deploy | Two-deploy lag for new behavior (without `skipWaiting`) |
| Termination allowed any time | Low memory and battery cost | No in-memory state; warm-up latency on first event |
| Byte-diff update check | Cheap, automatic | Whitespace-only edits ship a "new" worker |

### Common failure modes
- **The worker pins a stale `index.html`.** Cause: cache-first on the navigation request; user can never see the new HTML.
- **A new worker never activates.** Cause: a long-lived tab is still controlled; missing `skipWaiting()`.
- **First load after install is uncontrolled.** Cause: working as designed; missing `clients.claim()` if you wanted immediate control.
- **`event.respondWith()` rejects, browser shows a network error.** Cause: unhandled exception in the async handler; always `catch` and return a fallback `Response`.
- **Worker dies mid-request.** Cause: long-running work outside `event.waitUntil()`; the browser doesn't know it's busy.
- **Quota exceeded.** Cause: unbounded cache growth; nothing evicts the Cache API for you. Use LRU or explicit max-entry caps.
- **CORS / opaque responses fill the cache with 0-byte holes.** Cause: storing `response.type === 'opaque'` responses; they count against quota at a padded size and can't be inspected.

## Why

### Why the design constraints exist
**No DOM access** — the worker runs in parallel with any number of pages. If it could read or write the DOM, every property access would need cross-thread synchronization, and JavaScript's single-threaded determinism would dissolve. Message-passing forces an explicit, serializable interface.

**HTTPS-only** — a Service Worker is a network MITM running with the page's authority. If a coffee-shop Wi-Fi could install one over plain HTTP, every site you ever visited from that network could be silently rewritten on future visits. TLS is the prerequisite for trusting that the script came from the claimed origin.

**Staged install/activate** — at deploy time, there are already pages loaded against the previous worker, holding references to the old asset URLs. If the new worker activated instantly and wiped the old cache, those tabs would break the moment they next requested a resource. The waiting state is the version-skew guard.

**Allowed to be killed and resurrected** — on mobile, keeping a JS context alive per origin would burn battery and memory across dozens of installed sites. The spec chose: assume cold start, store everything you need in `caches`/IndexedDB, and treat in-memory state as scratch space.

### Why it looks the way it does
An obvious alternative was "make the cache declarative" (the AppCache approach: a manifest of URLs the browser caches). It failed because real caching policies are conditional — different strategies per route, version negotiation, fallback chains — and a static manifest can't express them. The Service Worker design replaces the manifest with a turing-complete event handler. You pay for that with a sharper foot-gun (a broken worker can brick the site for returning visitors), and you mitigate it with the 24-hour script-cache cap, the byte-diff update check, and `clients.claim` only working when you explicitly opt in.

### Why it matters now
In 2026, Service Workers are the foundation of the entire PWA story — install prompts, offline behavior, push, background sync — and they've quietly become table stakes for any site that wants to feel fast on flaky connections. They are also what makes browser-side request mocking (MSW) and edge-worker portability (same `fetch` event signature as Cloudflare Workers) viable. The API is past the churn phase; the spec is stable, all evergreen browsers ship it, and the interesting movement is in the surrounding APIs (Background Fetch, Periodic Sync, Web Push), not the core.

## Open questions / things to verify in practice
- How long does my worker actually stay alive on this device? Instrument with `console.log` timestamps across events and observe.
- What's the cache quota on this origin in this browser? Call `navigator.storage.estimate()`.
- Does my deploy break open tabs if I forget to bump the cache name? Test with two tabs and a forced redeploy.
- Does `skipWaiting()` cause visible breakage when the new worker serves new assets to an old DOM? Diff the version skew window.
- What happens to in-flight `fetch` events when the worker is terminated mid-handler? Reproduce by throttling and waiting.
- Is `updateViaCache: 'none'` worth the extra request, or does the default `imports` policy cause you any real staleness?
