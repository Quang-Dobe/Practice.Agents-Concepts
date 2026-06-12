# Hydration — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

Hydration is the load-bearing piece in every SSR-capable JS framework you'll ship in 2026. On a Next.js App Router e-commerce site, hydration is what turns the server-rendered product grid into something that can open a quick-view modal. On a Nuxt-based publication, it's what makes the comment widget come alive after the article HTML has already painted. On an Astro docs site, it's the thing you're *deliberately avoiding* for 95% of the page and selectively enabling for the search bar.

You'll feel it most acutely the first time CI catches a hydration mismatch in a PR diff that "only changed copy," or when a Sentry alert spikes because a date formatter ran in `Europe/Paris` on the edge and `America/Los_Angeles` in the browser. The patterns below are the muscle memory experienced frontend engineers build to keep those moments rare.

## Best practices

### 1. Gate browser-only logic behind effects, not render
**Do:** Read `window`, `localStorage`, `navigator`, `matchMedia`, or anything else browser-only inside `useEffect` (React) / `onMounted` (Vue) / `onMount` (Svelte) and store the result in state. Render a server-safe default on the first pass.
**Why:** During SSR there is no `window`. During hydration the client must produce the exact tree the server produced or React 18 will discard that subtree and re-render it ([React `hydrateRoot`](https://react.dev/reference/react-dom/client/hydrateRoot)).
**Avoid:** `const theme = typeof window !== 'undefined' ? localStorage.theme : 'light'` directly in JSX — guaranteed mismatch on every reload.

### 2. Make server and client render from the same data
**Do:** Pass server-fetched data into the client tree via the framework's state envelope (`__NEXT_DATA__`, `__NUXT__`, SvelteKit's `data`). Never let the client refetch the same query during the first render.
**Why:** Two fetches against the same endpoint at slightly different times will return different results — pagination cursors shift, "5 minutes ago" becomes "6 minutes ago," feature flags re-evaluate. Hydration sees that as a mismatch.
**Avoid:** Calling `fetch` inside a client component's render path for data the server already rendered with.

### 3. Use `suppressHydrationWarning` only on the offending leaf
**Do:** Apply `suppressHydrationWarning` to the single `<time>` or `<span>` whose content you *know* must differ (a relative timestamp, a per-tab UUID). React's docs are explicit that the flag is one level deep and intended as an escape hatch, not a fix ([React `hydrateRoot`](https://react.dev/reference/react-dom/client/hydrateRoot)).
**Why:** Wrapping it around a parent silences real bugs in the entire subtree. You will then ship a genuine mismatch and never see the warning.
**Avoid:** `<body suppressHydrationWarning>` "to fix Grammarly." That hides every future mismatch in the whole app.

### 4. Reach for `<ClientOnly>` / `client:only` for genuinely client-bound widgets
**Do:** Use Nuxt's `<ClientOnly>`, Astro's `client:only`, or Next's `dynamic(() => import(...), { ssr: false })` for components that read `window` at construction time — chart libraries (Recharts, Chart.js), rich-text editors, map widgets, anything that opens a WebSocket on mount.
**Why:** These components cannot produce stable SSR output, so paying the mismatch tax to render them on the server is pure cost. Skipping SSR for them is the documented escape hatch ([Vue SSR guide](https://vuejs.org/guide/scaling-up/ssr.html#client-specific-code), [Next.js dynamic import](https://nextjs.org/docs/app/api-reference/functions/dynamic)).
**Avoid:** Wrapping the entire route in `<ClientOnly>`. You just rebuilt CSR with extra steps and lost FCP/SEO.

### 5. Defer expensive non-critical components
**Do:** For below-the-fold widgets that don't need SEO — recommendations carousels, embedded video players, third-party comment threads — load them via `next/dynamic` with `loading: () => <Skeleton/>`, Astro's `client:visible`, or Nuxt's lazy-hydration helpers.
**Why:** Hydration cost scales with the bundle size of components in the tree. A 200 KB comment widget hydrating on the critical path delays TTI for every visitor, including those who never scroll to it ([web.dev — Reduce JavaScript payloads](https://web.dev/articles/reduce-javascript-payloads-with-code-splitting)).
**Avoid:** Hydrating a full marketing footer that just renders links and a newsletter form on every page load.

### 6. Budget JS per route and treat it as a hydration tax
**Do:** Set a per-route JS budget in CI (Lighthouse CI, `size-limit`, `@next/bundle-analyzer`). When a PR pushes a route over budget, the diff has to justify the cost.
**Why:** Every kilobyte in the route bundle is JS that must download, parse, and run before that subtree is interactive. Hydration time is roughly linear in component count and bundle size ([web.dev — Rendering on the web](https://web.dev/articles/rendering-on-the-web)).
**Avoid:** Letting one team ship a 400 KB date picker into the global layout because "it's a shared component."

### 7. Default non-interactive UI to server components
**Do:** In Next App Router, keep components server by default. Mark only the leaves that actually need state, effects, or browser APIs with `'use client'`. Push the `'use client'` boundary as deep into the tree as it will go ([Next.js — Server and Client Components](https://nextjs.org/docs/app/getting-started/server-and-client-components)).
**Why:** Server components ship zero JS to the client and skip hydration entirely. Every parent you convert to a server component shrinks the hydration surface area.
**Avoid:** Slapping `'use client'` on the root layout so child components can use hooks "for convenience." The whole tree below becomes a hydration target.

### 8. Use streaming SSR + Suspense for slow data
**Do:** Wrap slow async subtrees in `<Suspense>` so React can stream their HTML in and hydrate them selectively. Combined with `useDeferredValue` this keeps input responsive while heavy lists render ([Patterns.dev — Selective Hydration](https://www.patterns.dev/react/react-selective-hydration/)).
**Why:** A single slow query no longer blocks the shell. Users can click an already-hydrated nav while the comments boundary is still loading.
**Avoid:** A top-level `await` that gates the whole page on the slowest fetch.

### 9. Pick the right architecture for the page type
**Do:** Match the rendering strategy to the page's interactivity ratio. Mostly-static content (marketing, docs, blogs) → Astro islands or SSG with minimal hydration. Heavy app shell with personalization → Next App Router with RSC. Latency-critical interactivity on slow devices → consider Qwik's resumability.
**Why:** Choosing full hydration for a static page is paying TTI cost for nothing. Choosing islands for a dashboard means fighting your framework to share state across islands.
**Avoid:** Picking the framework first and forcing every page into its default model.

### 10. Treat every hydration warning as a real bug
**Do:** Fail CI on hydration warnings in tests (Playwright + console assertion, or React's `onCaughtError` callback). Triage each one — they almost always indicate either non-determinism, stale CDN HTML, or a third-party script.
**Why:** React 18 patches what it can but still discards mismatched subtrees, which means real users see flicker or lost client state. The warning is the only signal that this is happening in production.
**Avoid:** "It's just a dev warning, it works in prod" — it doesn't; it's the same code path, just quieter.

## Anti-patterns to recognize

- **`Date.now()` / `Math.random()` in render**: Looks harmless because each call returns a value. In production the server and client clocks differ, so the first paint mismatches every time. Compute these in `useEffect` and store in state, or pass the server's value as a prop.
- **`typeof window !== 'undefined'` ternaries in JSX**: Looks like defensive coding. It produces one tree on the server and another on the client — the textbook mismatch. Replace with a mount-flag pattern (`const [mounted, setMounted] = useState(false)` + effect).
- **Wrapping the whole app in `<NoSSR>` to "fix the warning"**: Eliminates the mismatch by eliminating SSR. You've thrown out FCP, SEO, and the social-card preview to silence a log line. The actual fix is to isolate the offending leaf.
- **Reading `localStorage` during render for theme**: A classic — the server has no `localStorage`, so it picks a default; the client picks the stored value; the page flashes. Use a cookie read server-side, or a blocking inline script that sets the class on `<html>` before React boots.
- **Third-party widgets that mutate the DOM before hydration**: Chat bubbles, A/B test injectors, Optimizely. They insert nodes the framework didn't render, the reconciler walks into them, and hydration breaks. Load them with `strategy="afterInteractive"` (Next's `<Script>`) and target a portal root they can't touch.
- **Stale HTML + fresh JS**: A CDN serves yesterday's cached HTML alongside today's bundle with a new prop shape. Pin HTML cache TTLs short, or include a build hash in the cache key so they invalidate together.
- **A/B flags evaluated separately on server and client**: The server bucket says "control"; the client SDK rolls again and gets "treatment." Always pass the bucket assignment from server to client through the state envelope and have the client read from there.
- **Date picker locales without an explicit `timeZone`**: `Intl.DateTimeFormat` falls back to the runtime's zone. Node's zone is wherever the container lives; the browser's is the user's. Always pass `{ timeZone: 'UTC' }` (or the user's stored zone) explicitly.

## Real-world usage patterns

**E-commerce product page (Next.js App Router)**: Product info, price, and image gallery render as server components — zero hydration cost on the bulk of the page. The "Add to cart" button, quantity selector, and reviews carousel are `'use client'` islands. The cart drawer is `dynamic(() => import('./CartDrawer'), { ssr: false })` because it reads from `localStorage` for guest carts. *Lesson*: the cart drawer's `ssr: false` choice is what unblocks the rest of the page from chasing localStorage mismatches up the tree.

**Content site with comments (Astro)**: Article body, sidebar, and footer ship as static HTML with no JS. The comment thread is a single React island with `client:visible` so it only hydrates when scrolled into view. *Lesson*: islands force you to think about state sharing up-front. The "like" button can't easily talk to a header counter unless you wire a `nanostores`-style signal. That friction is the price of near-zero baseline JS.

**SaaS dashboard (Remix / React Router 7)**: The whole shell hydrates because every panel is interactive. Heavy charts (`Recharts`, `visx`) are deferred with `React.lazy` + `<Suspense>`. The team enforces a per-route 120 KB budget in CI and keeps the chart library out of the shared chunk. *Lesson*: when you can't escape full hydration, route-level code splitting and a hard budget are the only things keeping TTI stable as the app grows.

**News site with personalization (Nuxt 3)**: Article HTML is SSR'd per request with the reader's region in the URL. The "for you" recommendation strip uses `<ClientOnly>` because its ranking model runs in the browser against IndexedDB history. *Lesson*: per-user widgets often want CSR-only behavior inside an otherwise SSR'd page; `<ClientOnly>` is the right granularity, not the page or the app.

## Operational checklist

- **Monitoring**: Is there a production console-error pipe (Sentry, Datadog RUM) that captures hydration warnings with stack traces?
- **CI gate**: Do Playwright/Cypress tests fail when `console.error` fires during initial navigation?
- **Bundle budgets**: Is there a per-route JS budget enforced in CI, and does the PR template surface the diff?
- **Determinism audit**: Has anyone grepped the codebase for `Date.now`, `Math.random`, `new Date(`, `toLocaleString`, `typeof window` in render paths?
- **Third-party scripts**: Are all injected scripts loaded with `afterInteractive` or `lazyOnload`, and do they target portal roots, not the framework root?
- **Theme/auth flicker**: Does the initial paint match the user's stored theme and auth state without a visible flash?
- **A/B testing**: Is the variant assignment serialized from server to client, or does the SDK re-roll on hydration?
- **Stale HTML defense**: Does the CDN's HTML cache key include the build hash, so an HTML/JS skew is impossible?
- **Onboarding**: Does the team README spell out the `'use client'` boundary rule and the mount-flag pattern for browser APIs?

## How this topic typically evolves in a codebase

Teams start with whatever the framework default is — `create-next-app`, `nuxi init`, `npm create svelte` — and ship features without thinking about hydration. The first six months are productive. Then a real production incident lands: a hydration mismatch on the checkout page traced to a `toLocaleString` call, or a Lighthouse regression because someone added a 300 KB charting library to the shared layout. The team adds a console-error monitor and a bundle budget, and starts the slow work of pushing `'use client'` boundaries deeper.

The painful migration point comes around the time the app reaches ~50 routes. The shared layout has accumulated client-only logic — auth context, theme provider, feature flags, analytics — and every page pays its hydration tax. Migrating to RSC (or to Astro islands, or to a Qwik rewrite) is no longer a weekend job; it requires re-architecting context providers, splitting the design system into server-safe and client-safe variants, and dealing with every third-party library that assumed it would run during render.

The mature endpoint is a hybrid: server components for static structure, narrow client islands for interactive leaves, deferred hydration for below-the-fold widgets, and a clear per-route budget that prevents regression. The teams that get here treat hydration cost as a first-class metric next to FCP and CLS, not as something the framework handles invisibly.

## Further reading

- [React — `hydrateRoot`](https://react.dev/reference/react-dom/client/hydrateRoot) — canonical reference for the API, the warning model, and `suppressHydrationWarning`'s exact semantics.
- [Next.js — Server and Client Components](https://nextjs.org/docs/app/getting-started/server-and-client-components) — the official rules for where the boundary should go in App Router code.
- [Next.js — `react-hydration-error` message](https://nextjs.org/docs/messages/react-hydration-error) — the framework's own debugging checklist; the same one their error overlay links to.
- [Vue 3 SSR Guide — Hydration Mismatch](https://vuejs.org/guide/scaling-up/ssr.html#hydration-mismatch) — covers the Vue-specific quirks and the `<ClientOnly>` escape hatch.
- [Astro — Islands Architecture](https://docs.astro.build/en/concepts/islands/) — the case for shipping less JS by default, with the `client:*` directive reference.
- [Patterns.dev — Selective Hydration](https://www.patterns.dev/react/react-selective-hydration/) — the clearest explanation of how Suspense boundaries interact with hydration priority in React 18.
- [web.dev — Rendering on the web](https://web.dev/articles/rendering-on-the-web) — Google's rendering-strategy decision guide; still the best framing of the trade space.
