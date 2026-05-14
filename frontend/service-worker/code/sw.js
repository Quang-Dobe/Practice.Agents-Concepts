// Compiled from sw.ts (TypeScript stripped of `as const` and the `declare` /
// type annotations). The browser loads THIS file at /sw.js; the .ts version
// is the source of truth for editing. Recompile with: npx tsc sw.ts --target ES2022 --lib ES2022,WebWorker --outFile sw.js
// (or just `npx tsc` if you keep the included tsconfig.json).

const CACHE_NAME = 'app-shell-v1';

const SHELL_URLS = [
  '/',
  '/index.html',
  '/style.css',
];

// ---- install: pre-populate a versioned cache with the app shell ----------
self.addEventListener('install', (event) => {
  event.waitUntil(
    (async () => {
      const cache = await caches.open(CACHE_NAME);
      await cache.addAll(SHELL_URLS);
    })(),
  );
});

// ---- activate: delete caches whose name is not in our allow-list ---------
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

// ---- fetch: cache-first with network fallback ----------------------------
self.addEventListener('fetch', (event) => {
  // `respondWith` MUST run synchronously inside this handler. All async work
  // happens inside the promise we pass to it.
  event.respondWith(cacheFirst(event.request));
});

const cacheFirst = async (request) => {
  const cache = await caches.open(CACHE_NAME);

  const cached = await cache.match(request);
  if (cached) {
    // Echo a header so the page can see the worker handled it.
    const headers = new Headers(cached.headers);
    headers.set('x-served-from', 'service-worker-cache');
    return new Response(cached.body, {
      status: cached.status,
      statusText: cached.statusText,
      headers,
    });
  }

  const response = await fetch(request);

  // Only cache successful, non-opaque GETs. Opaque responses count against
  // quota at a padded size and can't be inspected; non-GETs throw in `put`.
  if (response.ok && response.type !== 'opaque' && request.method === 'GET') {
    // Bodies are single-use streams — clone before storing.
    cache.put(request, response.clone());
  }

  return response;
};
