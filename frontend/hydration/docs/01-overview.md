# Hydration — Overview

> Hydration is the process of taking server-rendered HTML and "waking it up" in the browser by attaching JavaScript event listeners and component state so the page becomes interactive.

## The 30-second version

A server renders your component tree to a string of HTML and ships it to the browser. The user sees real content immediately — text, layout, images — but clicking anything does nothing yet, because no JavaScript has run. Then the framework's JS bundle loads, walks the same component tree on the client, and reconciles itself against the existing DOM: same nodes, same order, now wired up with handlers and state. That second pass is hydration. You get fast first paint and SEO-friendly HTML without giving up a client-side interactive app.

## The mental model

Think of the server response as an unfurnished house that's already built — walls, floors, windows in the right places. The user can walk through it the moment they arrive. A moving truck (the JS bundle) shows up a few seconds later carrying the appliances, light switches, and remote controls. The hydration step is the movers walking room by room, matching each appliance to its outlet, and confirming the layout matches the blueprint they brought. If a wall is in a different spot than the blueprint says, they complain loudly — that's a hydration mismatch.

The critical detail: the DOM is **not** thrown away and rebuilt. Hydration adopts what's already there. That's the whole point — re-rendering from scratch would defeat the speed win the server gave you.

## What it is NOT

- Not **client-side rendering (CSR)**. CSR ships a near-empty `<div id="root">` and asks the browser to render everything from JS. No HTML until JS runs.
- Not **plain server-side rendering (SSR)**. Pure SSR sends HTML and stops. The page is visible but inert — no React state, no `onClick`.
- Not **server-side templating** (Jinja, ERB, Razor). Those render HTML once and reload the page for every interaction; there's no client-side component tree to reconcile against.
- Not **static site generation (SSG)** by itself, though SSG output usually gets hydrated the same way SSR output does.

## When you would reach for it

- You need fast First Contentful Paint and good SEO, but the app has rich client-side interactivity (dashboards, e-commerce, social feeds).
- You're using a meta-framework that defaults to it: Next.js, Nuxt, SvelteKit, SolidStart, Remix.
- Your content is partly dynamic per-user (logged-in state, personalization) so a pure static cache isn't enough.

## When you would NOT reach for it

- A pure marketing site with no interactivity — ship static HTML and a sprinkle of vanilla JS; you don't need a framework runtime.
- An internal tool behind a login where TTI matters more than FCP — a CSR single-page app is simpler.
- A content site that's truly static and SEO-driven — SSG without hydration (or with islands) gives you smaller bundles.

## Key vocabulary (just enough to keep reading)

- **SSR** — server-side rendering; producing HTML on the server per request.
- **SSG** — static site generation; producing HTML once at build time.
- **CSR** — client-side rendering; the browser builds the DOM from JS.
- **FCP** — First Contentful Paint; when the user first sees real content.
- **TTI** — Time To Interactive; when the page actually responds to clicks.
- **Hydration mismatch** — server HTML and client render disagree; React logs an error and may discard the server tree.
- **`hydrateRoot`** — the React 18+ API that hydrates a server-rendered tree (replaces the older `ReactDOM.hydrate`).
- **Partial / progressive hydration** — hydrating only some components, or hydrating them over time instead of all at once.
- **Islands architecture** — ship mostly static HTML with isolated interactive "islands" that hydrate independently (Astro, Fresh).
- **Resumability** — Qwik's alternative to hydration; the server serializes enough state that the client can pick up without re-executing component code.

## What's next

The next document, `02-deep-dive.md`, answers What / Where / When / How / Why in detail: the exact reconciliation algorithm React uses in `hydrateRoot`, where mismatches come from, how Next.js streams hydration with Suspense, and how islands, progressive hydration, React Server Components, and Qwik-style resumability each attack the hydration cost from a different angle.
