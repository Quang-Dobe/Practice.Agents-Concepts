# Service Worker — Overview

> A Service Worker is a JavaScript file the browser runs in its own background thread, sitting between your web page and the network so it can intercept and answer requests on your terms.

## The 30-second version
A Service Worker is a script you register from a page once, after which the browser keeps it on the side — separate from any tab — and lets it observe every network request that comes from pages inside its scope. It can answer those requests from a cache, modify them, or pass them through. That single capability is what makes the modern web feel "app-like": pages that open instantly, work offline, receive push notifications, and quietly sync data in the background. Engineers care because it is the official, standards-track way to take control of the network layer in the browser without a native app.

## The mental model
Picture every `fetch()` your page makes — every image, every JSON call, every script — as a letter dropped into a mailbox. Without a Service Worker, that letter goes straight to the postal service (the network). With a Service Worker, you have installed a **personal mail clerk** in your house. Every outgoing letter passes through the clerk first. The clerk reads the address and decides:

- "I already have a copy of this in my filing cabinet — here you go" (cache hit).
- "Let me run to the post office and bring back the reply" (network passthrough).
- "The post office is closed today; here is last week's copy with a note" (offline fallback).

That clerk is a programmable proxy, written by you, running in JavaScript, living in the browser. They don't read the contents of your house — they have no idea what the DOM looks like, can't touch `window` or `document`, and never block the page's main thread. They only see the mail.

## What it is NOT
- **Not a Web Worker.** A Web Worker is also a background thread, but it belongs to a single page and exists to offload CPU work. A Service Worker outlives any page and exists to intercept network traffic.
- **Not the Cache API.** The Cache API is a key/value store of `Request`/`Response` pairs. Service Workers use it heavily, but the cache works fine without a Service Worker, and a Service Worker can ignore the cache entirely.
- **Not Application Cache (AppCache).** AppCache was the previous attempt at offline web. It is deprecated and removed. Service Workers replaced it.
- **Not a server.** It runs on the user's device, in their browser, scoped to one origin.

## When you would reach for it
- You want the app to open and render something useful with no network connection.
- You want fine-grained control over caching that HTTP cache headers can't express (stale-while-revalidate, cache-first for shells, network-first for APIs).
- You need to receive push notifications when the site isn't open.
- You want background sync — retrying a failed POST once the user is back online.
- You are building a Progressive Web App and need an installable, offline-capable shell.

## When you would NOT reach for it
- The site is a single static landing page with no offline requirements — HTTP caching is enough.
- You need to manipulate the DOM in the background. Service Workers can't touch the DOM; use a Web Worker plus `postMessage` instead.
- You are inside an environment without HTTPS and can't get a certificate. Service Workers only run over HTTPS (with `localhost` as the dev-time exception).
- You need synchronous access to page state. The worker is event-driven and decoupled by design.

## Key vocabulary (just enough to keep reading)
- **Registration** — the one-time call from a page (`navigator.serviceWorker.register('/sw.js')`) that tells the browser to install this script.
- **Scope** — the URL prefix the worker controls. A worker at `/sw.js` controls everything under `/`; one at `/app/sw.js` controls only `/app/`.
- **Lifecycle states** — `parsed`, `installing`, `installed` (a.k.a. *waiting*), `activating`, `activated`, `redundant`. A worker moves through these as it is downloaded, set up, and eventually replaced.
- **`install` event** — fires once per worker version; the place to pre-populate caches.
- **`activate` event** — fires when this worker is about to take control; the place to clean up old caches.
- **`fetch` event** — fires for every network request inside scope; the place where the "mail clerk" makes its decision.
- **Controlled client** — a page (tab, iframe) currently governed by an active worker.
- **Cache API** — the `caches` global, a request/response key-value store the worker reads and writes.
- **`skipWaiting` / `clients.claim`** — opt-in calls that let a new worker take over immediately instead of waiting for every tab to close.

## What's next
The next document, `02-deep-dive.md`, answers What / Where / When / How / Why in detail — the full lifecycle ordering, the event objects, how scope resolution actually works, and the common caching strategies (cache-first, network-first, stale-while-revalidate) by name.
