# Service Worker

A Service Worker is a JavaScript file the browser runs in its own background thread, sitting between a web page and the network so it can intercept and answer every request the page makes. You register it once from a page, and from then on the browser keeps it on the side — separate from any tab — letting it observe fetches inside its scope and respond from a cache, modify them, or pass them through. It is the standards-track way to take control of the network layer in the browser without writing a native app.

Engineers reach for a Service Worker when they want pages that open instantly, work offline, receive push notifications, or quietly sync data in the background. It is the engine behind Progressive Web Apps and the only way to express caching rules that HTTP cache headers cannot — stale-while-revalidate, cache-first for app shells, network-first for APIs. It is not the right tool for sites with no offline requirements, environments without HTTPS, or work that needs synchronous access to the DOM, because a worker has no `window` or `document` and is intentionally decoupled from any single page.

The mental model is a personal mail clerk you install in your house. Every outgoing letter — every `fetch` your page makes — passes through the clerk first, who reads the address and decides whether to answer from their filing cabinet, run to the post office, or hand back last week's copy with a note. The clerk is a programmable proxy, written by you, running in JavaScript, scoped to one origin.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/frontend/service-worker/
