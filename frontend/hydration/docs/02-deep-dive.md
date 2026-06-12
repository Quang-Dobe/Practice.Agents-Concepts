# Hydration — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

Hydration is the process by which a client-side UI framework adopts a pre-existing DOM subtree produced by a server (SSR) or build step (SSG), walks its own component tree against that DOM in lockstep, attaches event listeners, restores reactive state from a serialized payload, and runs any mount-time effects — without recreating the DOM nodes that are already on the page.

Formally, given a server-rendered subtree `S_dom` and a client component tree `C`, hydration is a tree-walk that proves the assertion `render(C) ≡ S_dom` while populating the framework's runtime data structures (React's Fiber tree, Vue's vnode tree, Svelte's component instances). The walk terminates with each DOM node "owned" by exactly one component instance. If the assertion fails — node mismatch, attribute disagreement, text difference — the framework either bails out and re-renders the subtree from scratch (Vue, older React) or logs an error and patches what it can (React 18+).

### The core building blocks

- **Server renderer** — `renderToString` / `renderToPipeableStream` (React), `renderToString` (Vue), Svelte's `render` from `svelte/server`, SolidJS's `renderToStringAsync`. Produces HTML and, separately, a serialized state payload.
- **Serialized state envelope** — the JSON blob the server emits alongside HTML so the client doesn't have to re-fetch. In Next.js this is `__NEXT_DATA__`; in Nuxt it is `__NUXT__`; in SvelteKit it's embedded in a `<script>` tag the kit runtime reads on boot.
- **Client hydration entry point** — `hydrateRoot(domNode, <App/>)` in React 18+, `createSSRApp(App).mount('#app')` in Vue 3, `hydrate(App, { target })` in Svelte, `hydrate(() => <App/>, root)` in SolidJS.
- **Reconciler** — the framework's tree-walking diff engine. For React this is the Fiber reconciler in hydration mode; for Vue, the `hydrate` function in `@vue/runtime-core`; for Svelte, generated code that walks existing nodes by index.
- **Event delegation system** — most frameworks attach a small number of root-level listeners and dispatch synthetic events down to the matched component, rather than attaching one DOM listener per `onClick`.

### How it relates to the broader landscape

Hydration sits inside the family of *isomorphic / universal rendering* strategies. Its siblings differ in how much work the client has to repeat:

| Strategy | Server work | Client work | TTI |
|---|---|---|---|
| CSR | none | full render + mount | slow (after JS) |
| SSR, no hydration | HTML | none — page is inert | n/a (no JS app) |
| SSR + hydration | HTML + state | re-render + reconcile + attach | medium |
| SSG + hydration | HTML at build | same as SSR + hydration | medium |
| Islands (Astro, Fresh) | HTML + state | hydrate only marked components | fast |
| RSC + selective hydration | HTML + RSC payload | hydrate only client components | fast |
| Resumability (Qwik) | HTML + serialized framework state | resume on first interaction | near-zero |

Hydration is the *default* for SSR-capable JS frameworks; islands and resumability are responses to its cost.

## Where

### Where it runs in the stack

Hydration is a **browser-runtime** concern, but it depends on three other layers cooperating:

1. **Server / build pipeline** — runs the SSR renderer, emits HTML and the state envelope.
2. **Bundler** — produces the client JS bundle that contains the same component code the server ran, plus the framework runtime in hydration mode. Code-splitting decisions made here directly determine hydration granularity.
3. **Framework runtime** — the Fiber/vnode/instance machinery that performs the actual reconciliation in the browser.

The "match" happens at component boundaries during this client tree walk. React, for example, compares fiber-by-fiber, expecting each fiber's output to align with the next DOM node from the server. It does not text-diff the HTML string; it walks the live DOM and the new fiber tree together.

### Where you typically encounter it

- **Next.js App Router** — RSC by default; `'use client'` components hydrate via `hydrateRoot`, streamed by `renderToPipeableStream` ([Next.js docs](https://nextjs.org/docs/app/getting-started/server-and-client-components)).
- **Nuxt 3** — Vue 3 `createSSRApp` under the hood, with `<ClientOnly>` as the standard mismatch escape hatch ([Vue SSR docs](https://vuejs.org/guide/scaling-up/ssr.html)).
- **SvelteKit** — hydration per-route, controllable via the `csr` page option ([SvelteKit glossary](https://svelte.dev/docs/kit/glossary)).
- **Remix** — React, hydrates the full route tree.
- **SolidStart** — SolidJS `hydrate`, fine-grained reactivity instead of VDOM diff.
- **Astro** — explicit `client:load`, `client:visible`, `client:idle`, `client:media` directives per island ([Astro docs](https://docs.astro.build/en/concepts/islands/)).
- **Qwik / QwikCity** — does not hydrate; resumes ([Qwik docs](https://qwik.dev/docs/concepts/resumable/)).

### Ecosystem and tooling

- **For diagnosing mismatches**: the React DevTools "Components" tab, browser-extension detection scripts, the Next.js dev overlay, `@welldone-software/why-did-you-render`.
- **For deferring hydration**: `react-lazy-hydration`, `astro`'s `client:*` directives, Nuxt's `<LazyHydrate>` (via `@nuxt/lazy-hydration` style libraries).
- **For replacing hydration**: Qwik (resumability), Marko (server-driven streaming with auto-islands), HTMX (no hydration — re-renders fragments from the server).
- **For state serialization**: `superjson`, `devalue` (used internally by SvelteKit), Next's built-in JSON serializer with limited type support.

## When

### When the topic emerged and why

The term hydration appears in the React ecosystem around 2015 with the introduction of `ReactDOM.render` accepting server-rendered markup, and is named explicitly with `ReactDOM.hydrate` in React 16 (2017). Before SSR-aware reconciliation, libraries like Meteor and `react-dom-stream` patched HTML manually or threw away server markup on mount. The motivation was straightforward: pure CSR's blank-page-then-pop problem killed SEO and Core Web Vitals on content-heavy sites, while pure SSR (Rails, Django) couldn't deliver SPA-like interactivity. Hydration was the compromise. React 18 (2022) reshaped it again with `hydrateRoot`, streaming SSR via `renderToPipeableStream`, and **selective hydration** through Suspense boundaries ([React 18 working group](https://github.com/reactwg/react-18/discussions/37)).

### When to use it in a project

Reach for SSR + hydration when:

- The site is content-driven and needs FCP under ~1.5s on mobile.
- SEO or social-card preview matters and content is dynamic per-request (logged-in user, A/B, geo).
- The app is interactive enough that a pure templating stack (Rails, Django, Razor Pages) would force a roundtrip on every click.
- You're already on a meta-framework that defaults to it (Next.js, Nuxt, SvelteKit, SolidStart).

Reach for **islands or RSC** specifically when most of the page is static and only a few regions are interactive — product pages, marketing sites, docs, blogs with a comment widget.

Reach for **resumability** when interactivity must feel instant on slow devices and the page's serialized framework state is small enough to fit in the HTML payload without ballooning bytes-over-wire.

### When NOT to use it

Avoid hydration when:

- The page has zero JS interactivity — ship plain HTML; you don't need a framework runtime at all.
- The app is behind auth and SEO/FCP don't matter — CSR is simpler, no SSR infra to run.
- The page is heavily dynamic per-keystroke (a complex editor) — the hydration cost happens once but the bundle is the same; you're paying for SSR without much FCP benefit if users dwell for minutes.
- You can't keep server and client render output deterministically equal (heavy use of `Date.now`, locale APIs, `window` checks scattered through the tree). The cost of fighting mismatches will exceed the win.

## How

### How it works under the hood

Take a React 18 SSR pipeline as the canonical case. The full lifecycle:

1. **Request arrives at server.** Next.js routes it to a page module.
2. **Server-side render.** `renderToPipeableStream(<App/>)` walks the component tree, calling each function component once, producing an HTML stream. Suspense boundaries that hit a pending promise emit a fallback in their HTML slot and continue rendering siblings.
3. **Stream flush.** `onShellReady` fires when the non-Suspended shell is complete; the server pipes HTML to the client. As suspended subtrees resolve, additional HTML chunks are appended, each wrapped in a `<template>` tag with a small inline script that swaps it into place.
4. **Serialized payload emitted.** Next embeds `self.__next_f.push([...])` chunks containing the RSC payload (a serialized description of the rendered tree, including props for client components).
5. **Client receives HTML.** The browser parses and paints. FCP happens here — well before any framework JS has executed.
6. **Client bundle loads.** The framework runtime and all `'use client'` components arrive (code-split per route).
7. **`hydrateRoot(container, <App/>)` runs.** React allocates a Fiber tree and begins a hydration render. For each fiber, it expects to find a matching DOM node at the current cursor position in `container`. It does not create new nodes; it adopts the existing ones.
8. **Event delegation attaches.** React installs delegated listeners at the root so that the matched fibers can receive synthetic events.
9. **Effects run.** `useEffect` callbacks fire after the initial commit, in the same order as a normal first render.
10. **Selective hydration kicks in.** If a Suspense boundary's JS hasn't arrived, React skips it and continues hydrating the rest. When the missing chunk arrives, that boundary hydrates on its own. If the user clicks inside a not-yet-hydrated boundary, React prioritizes hydrating that boundary first ([Patterns.dev — Selective Hydration](https://www.patterns.dev/react/react-selective-hydration/)).

The "matching" step in the React reconciler is a structural compare: tag name, key, and a small set of attributes. Text nodes are compared by string equality. When a mismatch is detected during this walk, React 18 logs an error and, for the offending subtree, falls back to a **two-pass render** — discard the server DOM in that subtree and re-render from scratch on the client.

Vue's behavior is similar but more aggressive: any mismatch logs a warning and **bails hydration entirely for the affected component**, discarding the server DOM and re-rendering ([Vue SSR docs](https://vuejs.org/guide/scaling-up/ssr.html)). Svelte walks the existing DOM by node index using compiled code; it's the cheapest of the four at runtime but the least forgiving of mismatches.

### Key trade-offs

| Choice | Gain | Cost |
|---|---|---|
| Hydrate the full tree (default React/Vue) | Single mental model, any component can be interactive | Every component's JS must download, parse, execute |
| Selective hydration via Suspense | Hydration starts before all JS loads; click-priority | Requires Suspense boundaries authored intentionally |
| Islands (Astro) | Near-zero JS for static parts | Cross-island state sharing is hard; needs explicit signals or stores |
| RSC | Server components ship zero JS | Server runtime cost; mental split between server/client component rules |
| Resumability (Qwik) | Near-zero TTI | Larger HTML payload; framework lock-in; smaller ecosystem |
| Defer hydration (`client:visible`, lazy-hydrate) | Lower TBT | Click before hydrate = no-op; needs UX consideration |

### Common failure modes

- **`Date.now()` / `Math.random()` in render** — server clock and client clock differ; mismatch on first paint.
- **Conditional render on `typeof window`** — server renders one branch, client renders the other.
- **Locale-dependent formatting** — `toLocaleString()` produces different output in Node vs browser; same for `Intl.DateTimeFormat` without an explicit `timeZone`.
- **Invalid HTML nesting** — `<div>` inside `<p>`, missing `<tbody>` inside `<table>`. The browser parser auto-corrects, the framework's vnode tree doesn't, mismatch follows ([Vue docs](https://vuejs.org/guide/scaling-up/ssr.html)).
- **Browser extensions injecting attributes** — Grammarly, password managers add `data-*` attributes before hydration. React 18 logs but tolerates attribute-only mismatches; older versions don't.
- **Reading `localStorage` during render** — only available on the client; common in theme switchers.
- **Stale CDN HTML + new JS bundle** — a deployed bundle expecting new props meets cached HTML from yesterday's render.

The accepted escape hatches: render the dynamic part only after mount (`useEffect` + state flag), gate it with `<ClientOnly>` (Nuxt) or `client:only` (Astro), or annotate the offending element with `suppressHydrationWarning` (React) — which is one-level-deep and explicitly **not** a fix, only a silencer ([React docs](https://react.dev/reference/react-dom/client/hydrateRoot)).

### How resumability differs

Qwik's resumability is structurally distinct, not just an optimization of hydration. Where hydration re-executes component functions on the client to rebuild the framework's in-memory state, Qwik serializes that state — listener locations as `on:click` attributes pointing at lazy-loaded chunk URLs, component boundaries as HTML comments, reactive state as JSON in a `<script type="qwik/json">` block. The first interaction triggers download of *only* the closure for that handler; the framework picks up from the serialized state without running parent components again ([Qwik resumable docs](https://qwik.dev/docs/concepts/resumable/)). The cost is on the wire (larger HTML) and in the mental model (you must write code that survives serialization).

## Why

### Why it exists

Hydration is the bridge between two contradictory requirements: *show something fast and let crawlers read it* (favors HTML from the server) and *behave like an app* (favors a client-side component tree with state and listeners). Without hydration, the only way to satisfy both was to render twice — once as HTML, once as a fresh client mount — and have the client mount visibly clobber the server output. Hydration makes that second mount *adopt* the first, eliminating the flash and the duplicate DOM allocation.

At a deeper level, it exists because the browser's HTML parser and a JS framework's vnode/Fiber tree don't share an internal representation. Hydration is the protocol that reconciles those two representations against a single set of DOM nodes.

### Why it looks the way it does

The obvious alternative — *serialize the full framework state on the server and ship it to the client so no re-execution is needed* — is what Qwik does. The reason mainstream frameworks didn't do this first is that serializing arbitrary JS closures, class instances, and effect graphs is hard. React's render functions can close over module-scope variables, third-party hooks, and provider context that don't have a clean JSON representation. Re-executing on the client is the path of least resistance: the same code runs twice and reaches the same state, with no serialization protocol to design.

The two-pass render fallback exists for the same reason. React *could* attempt to patch individual nodes on mismatch, but the reconciler's invariants assume tree consistency. Once you've lost that invariant in a subtree, throwing it away and re-rendering is the only safe move.

Selective hydration via Suspense exists because the all-or-nothing model didn't scale: a single slow `<Comments>` component shouldn't block the rest of the page from becoming interactive. Suspense boundaries give the framework a natural unit at which to split hydration work.

### Why it matters now

As of mid-2026, the frontier has clearly moved past "hydrate everything." React Server Components are the default in Next.js App Router, Astro's island model is the recommended path for content sites, and Qwik has demonstrated that near-zero TTI is achievable at the cost of a different programming model. Every modern framework's roadmap is dominated by *how to ship less hydration*, not how to make hydration faster. Understanding the mechanism matters because every design choice in those newer models — what counts as a client boundary, what gets serialized, when listeners attach — only makes sense once you know what hydration actually does and what it costs.

## Open questions / things to verify in practice

- On a real Next.js app, measure the gap between FCP and TTI with the Performance panel — is hydration actually the dominant cost, or is it your bundle parse time?
- Try forcing a mismatch (e.g. `{new Date().toString()}` in a server component) and observe the exact dev-overlay output in current Next.js vs the production behavior.
- Compare bundle sizes for the same page rendered as (a) all client components, (b) RSC + minimal `'use client'` leaves, (c) Astro with one React island. Which actually wins?
- In Qwik, inspect the HTML payload size on a non-trivial page — is the serialized state worth the FCP cost on a slow network?
- Test a selective-hydration scenario: click a button inside a not-yet-hydrated Suspense boundary and confirm React prioritizes it (network throttling helps reproduce).
- See `code/mvp.ts` (once written) for a runnable demo that exercises a deliberate mismatch and observes recovery.
