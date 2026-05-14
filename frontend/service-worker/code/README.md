# Service Worker - MVP Code

The smallest runnable demo: registration, full lifecycle (`install` + `activate` + `fetch`), and cache-first interception of real requests.

## What it demonstrates

- One-shot registration via `navigator.serviceWorker.register('/sw.js')`.
- Lifecycle in code: `install` precaches the shell with `event.waitUntil`; `activate` sweeps old caches via a versioned name; `fetch` intercepts in-scope requests with `event.respondWith`.
- Cache-first with network fallback as the concrete strategy. Comments show where network-first or stale-while-revalidate would slot in.
- Real offline support: HTML, CSS, and JS are served from cache when the network is gone.

## Prerequisites

- Node 20+ for `npx tsc` (optional — pre-compiled `sw.js` is checked in).
- Any static file server, e.g. `python3 -m http.server 8080`.
- A Chromium browser; its Application panel has the lifecycle controls. The worker is served from the site root so its scope is `/`.

## Run it

```bash
npx tsc                          # optional; compiles sw.ts -> sw.js
python3 -m http.server 8080      # serve the folder
# open http://localhost:8080 in a browser
```

## What to look for in DevTools

- Application > Service Workers: status reaches `activated and is running`.
- Application > Cache Storage > `app-shell-v1` contains `/`, `/index.html`, `/style.css`.
- Network tab: in-scope requests show "(ServiceWorker)" in the Size column.
- Network > Offline checkbox -> reload: the page still renders. That is the proof.

## What to try next

- Bump `CACHE_NAME` to `app-shell-v2`, recompile, reload. Watch `activate` delete the old cache.
- Comment out `event.waitUntil(...)` in `install` and observe a half-installed cache.
- Add an `await` before `event.respondWith(...)` and watch interception silently stop.
- Swap the body of `cacheFirst` for the network-first sketch in the doc comment.
