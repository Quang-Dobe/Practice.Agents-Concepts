# IntersectionObserver — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

On a content or e-commerce site, IntersectionObserver usually sits between the product grid and the network. It decides when a product image, a recommendations carousel, or a "you may also like" widget starts fetching. If the rules are wrong, LCP regresses or users on mobile data download 40 images they never scroll to.

In a social feed or activity timeline, it is the trigger for pagination (a sentinel at the bottom of the list), "seen" receipts, and autoplay/pause for video. There the failure mode is usually a feed that stops loading, or a read-receipt counter that double-counts.

In an ad-supported or analytics-heavy product, it is the measurement layer. Impression events, MRC-style viewability ("50% visible for 1 continuous second" for display, 2 seconds for video), and scroll-depth metrics are all IO plus a timer. In this setting the bugs show up on the finance dashboard, not in the UI.

Most of the time you meet it indirectly, through a framework primitive (Angular `@defer (on viewport)`, Astro `client:visible`, Next.js `<Link>` prefetch) or a hook (`useInView`). Knowing the model in `02-deep-dive.md § How` is what lets you debug those.

## Best practices

### 1. Try the declarative option before writing an observer
**Do:** Use `loading="lazy"` for below-the-fold `<img>`/`<iframe>`, `content-visibility: auto` for skipping render work, and CSS `animation-timeline: view()` for reveal-on-scroll effects. Write IO code only when you need a custom distance, a callback, or analytics.
**Why:** Native lazy loading is tuned by the browser (Chrome uses roughly 1250px ahead on fast connections and 2500px on slow ones) and needs zero JS, zero hydration, zero tests.
**Avoid:** A hand-rolled `img[data-src]` lazy-loader in 2026 just because the old codebase had one.

### 2. Never lazy-load what is in the initial viewport
**Do:** Render the hero/LCP image eagerly with a real `src` and `fetchpriority="high"`. Only apply IO-based or native lazy loading to content below the fold.
**Why:** An IO-gated image cannot start fetching until JS runs, the observer is created, and one rendering pass completes. web.dev measured LCP regressions from exactly this pattern on WordPress sites.
**Avoid:** Applying the same `<LazyImage>` component to every image on the page.

### 3. Share one observer per configuration
**Do:** Create one observer per unique `(root, rootMargin, threshold)` tuple and route entries through a `WeakMap<Element, handler>`. `react-intersection-observer` already does this internally.
**Why:** Chrome's implementers have confirmed a shared observer is cheaper, and Ben Nadel measured visibly slower deferred rendering with one observer per element in an Angular list. Per-element observers also mean per-element callback tasks.
**Avoid:** `new IntersectionObserver(...)` inside every list-item component.

### 4. Always branch on `isIntersecting`
**Do:** Start every callback loop with `if (!entry.isIntersecting) continue;` (or handle the leave case explicitly).
**Why:** Every `observe()` produces an initial entry, often with `isIntersecting: false`. Code that treats "callback fired" as "became visible" loads everything, or fires an impression for every card on mount.
**Avoid:** Using `intersectionRatio > 0` as the test. Edge-adjacent and zero-area targets break it.

### 5. Unobserve one-shot targets, disconnect on unmount
**Do:** Call `observer.unobserve(entry.target)` right after a lazy load or a single impression fires. In SPAs, `disconnect()` in the cleanup of the effect/directive that created the observer.
**Why:** Leftover registrations keep detached DOM subtrees reachable and keep the browser computing geometry for targets you no longer care about. Over a long session in a feed app this becomes a slow heap leak.
**Avoid:** Relying on garbage collection to clean up observers held in module-level singletons.

### 6. Keep the callback cheap and write-only
**Do:** Read geometry from the entry (`boundingClientRect`, `intersectionRect`), record IDs, and push heavy work to `requestIdleCallback` or a batched `requestAnimationFrame` write.
**Why:** A fling can deliver hundreds of entries in one task. Calling `el.getBoundingClientRect()` or reading `offsetHeight` inside the loop forces synchronous layout on every iteration and shows up as a long task on INP.
**Avoid:** Calling `fetch`, `setState`, and layout reads per entry inside the loop.

### 7. Pick thresholds you can actually reach
**Do:** Use `threshold: 0` for "about to appear", `0.5` for viewability, and compute "fully visible" as `entry.intersectionRatio >= 0.99` or `intersectionRect.height >= rootBounds.height` for tall targets.
**Why:** `threshold: 1` never fires for targets taller than the viewport, and fractional pixels under zoom cap the ratio at 0.9999. Impressions for tall cards silently go to zero on some devices.
**Avoid:** `threshold: [0, 0.01, 0.02, ... 1]` to fake a scroll-progress value. Use scroll-driven animations instead.

### 8. Use `rootMargin` in `px`, and `scrollMargin` for nested scrollers
**Do:** Prefetch ahead with `rootMargin: "0px 0px 400px 0px"` (bottom only for vertical feeds). For carousels, set `scrollMargin` (Baseline since September 2025) or use the carousel element as `root`.
**Why:** `rootMargin` only grows the root. A horizontal carousel still clips at its own edge, so slide 2 loads with a visible blank. Percentages resolve against the root size, which varies wildly between phone and 4K monitor.
**Avoid:** `rootMargin: "50%"` copied from a desktop-only prototype.

### 9. Pair viewability tracking with `visibilitychange` and a timer
**Do:** For "visible for N seconds", start a timer on enter, cancel on leave, and also cancel when `document.visibilityState === "hidden"`. Send the event once and `unobserve`.
**Why:** IO keeps reporting "intersecting" for a tab in the background as far as your timer knows, and callbacks are throttled there. Without the visibility check, a user who switches tabs generates impressions that ad buyers will later dispute.
**Avoid:** Counting an impression on the first `isIntersecting: true`.

### 10. Mock IO in unit tests, exercise it for real in E2E
**Do:** Install a controllable mock in the Jest/Vitest setup (for React, `react-intersection-observer/test-utils` provides `mockAllIsIntersecting`) and cover real scrolling in Playwright.
**Why:** jsdom has no IntersectionObserver, so tests either crash with `IntersectionObserver is not defined` or someone adds a no-op stub that never fires, and every "visible" branch goes untested.
**Avoid:** A global `window.IntersectionObserver = class { observe() {} }` stub with no way to trigger entries.

## Anti-patterns to recognize

- **Sentinel that stays visible**: infinite scroll where page 1 returns 5 short items and the sentinel never leaves the viewport. No state change means no new callback, so the feed stalls on large monitors. After appending, check whether the sentinel is still intersecting (from the latest entry or one `getBoundingClientRect()`) and fetch again, or `unobserve`/`observe` it to force a fresh initial entry.
- **Observer in a render function**: in React, `new IntersectionObserver` in the component body or an effect with unstable dependencies (`options` object literal). It reconnects on every render, re-emits initial entries, and fires duplicate analytics. Memoize options and depend on primitives, or use a shared-observer hook.
- **"Intersecting means seen"**: treating v1 intersection as proof the user saw the element. A `position: fixed` banner, an `opacity: 0` wrapper, or a closed `<details>` can cover it and IO still says `true`. For fraud-sensitive metrics, add v2 `trackVisibility` in Chromium and label non-Chromium numbers as geometry-only.
- **Explicit `root` that is not an ancestor**: passing a sibling scroll panel or a `ref` that points at the wrong wrapper. Nothing throws and nothing ever intersects, so the bug looks like "lazy images never load in the sidebar." Assert `root.contains(target)` in development builds.
- **IO as a scroll-position engine**: parallax, progress bars, or sticky-header shrink driven by dense thresholds. Callbacks are async and sampled per frame, so the effect lags and a fast fling skips targets. Use CSS scroll-driven animations or a passive scroll listener with rAF.
- **Premature polyfill**: shipping the W3C `intersection-observer` polyfill to every user. It adds bytes and a scroll-listener fallback for browsers that have had the API since 2019. Delete it unless analytics show real traffic from pre-Safari-12.1 clients.
- **Server-side construction**: touching `IntersectionObserver` at module top level in an SSR app. Node has no such global, so the build or first request throws. Create observers only in client lifecycle hooks (`useEffect`, `onMounted`, `afterNextRender`).

## Real-world usage patterns

**Media-heavy marketplace, product grid with 60 cards per page.** One shared observer with `rootMargin: "0px 0px 600px 0px"` swaps in image `src` and mounts the "add to cart" island only when a card is near. The lesson: the first row must be excluded from the lazy path. The team's LCP regression came from a single component that lazy-loaded row one, not from the observer itself.

**News site, scroll-spy table of contents.** Each `<h2>` is observed with `rootMargin: "0px 0px -70% 0px"`, so a heading becomes "active" once it enters the top 30% of the viewport. The non-obvious lesson: with several headings in that band at once, the callback gets several entries per task. You need to track the full set of intersecting headings and pick the top-most one, not "last entry wins," or the highlight flickers.

**Ad-supported publisher, viewability measurement.** An observer at `threshold: 0.5` starts a 1-second timer per slot. Timers pause on `visibilitychange` and the slot is unobserved after the impression. The lesson: impression counts diverged from the ad server's numbers until the team realized the ad server measured inside a cross-origin iframe, where `rootMargin` is ignored and `rootBounds` is `null`. Both sides need to agree on which frame does the measuring.

**Social feed, autoplay video.** Videos play at `threshold: 0.75` and pause below 0.25 (two thresholds, hysteresis in code). The lesson: a single threshold at 0.5 made videos thrash between play and pause when a user stopped scrolling with a card half-visible, which burned battery and triggered repeated media buffering.
## Operational checklist

- Monitoring: are LCP and INP tracked in RUM and checked against the lazy-load boundary? Are impressions deduplicated per target ID, with an alert if the impression-to-pageview ratio drops after a deploy?
- Failure handling: if the next-page fetch fails, can the sentinel re-trigger, or does infinite scroll stay dead until reload?
- Failure handling: is the "sentinel still visible after append" case tested on a tall viewport (for example 2560x1440)?
- Correctness: does every callback branch on `isIntersecting`, and does every `threshold: 1` target fit inside the root?
- Lifecycle: does every observer have a matching `disconnect()` on unmount, and do one-shot targets call `unobserve`?
- Security/privacy: are viewability numbers labelled as geometry-only (v1) where `trackVisibility` is unavailable, and is no IO-derived data used as proof against click fraud outside Chromium?
- Cost: does a wide `rootMargin` pull images or API calls for content most users never reach? Check bytes-per-session on mobile before and after.
- Testing: is there a controllable IO mock in the unit-test setup and at least one real-browser E2E scroll test?
- Onboarding: does the codebase document which shared hook or observer pool to use, so new engineers do not create ad-hoc observers?

## How this topic typically evolves in a codebase

Projects usually start with one copy-pasted snippet: a lazy-image component or an infinite-scroll hook with its own `new IntersectionObserver`. It works, so it gets copied. Six months later there are five slightly different wrappers, each with its own options, a few missing `disconnect()`, and at least one that treats the initial entry as "visible." Analytics impressions and lazy images are now coupled to the same accidental behaviour.

The painful migration point is consolidation. Moving to a shared observer pool or a single `useInView` hook changes timing subtly (entries batch together, initial entries arrive in a different task), so analytics numbers shift and someone has to explain the dip to product or ad sales. Plan that migration with a before/after comparison of impression counts, and ship it behind a flag.

Mature codebases end up with less IO code, not more. Native `loading="lazy"` replaces image lazy-loaders, `content-visibility: auto` replaces render-skipping, CSS scroll-driven animations replace reveal effects, and framework primitives (`@defer`, islands) absorb component deferral. What remains is a small, well-tested module for the cases CSS cannot express: pagination sentinels, viewability timers, and custom prefetch distances.

## Further reading

- [W3C Intersection Observer spec](https://w3c.github.io/IntersectionObserver/): the source of truth for initial entries, cross-origin margin rules, and threshold math.
- [MDN: Intersection Observer API](https://developer.mozilla.org/en-US/docs/Web/API/Intersection_Observer_API): the clearest reference for options, entry fields, and root/margin behaviour.
- [web.dev: The performance effects of too much lazy loading](https://web.dev/articles/lcp-lazy-loading): real field data on how lazy-loading above-the-fold images hurts LCP.
- [web.dev: Browser-level image lazy loading](https://web.dev/articles/browser-level-image-lazy-loading): Chrome's native distance thresholds, useful for deciding when you still need IO.
- [Ben Nadel: IntersectionObserver performance, many vs. shared](https://www.bennadel.com/blog/3954-intersectionobserver-api-performance-many-vs-shared-in-angular-11-0-5.htm): a concrete measurement of per-element vs. shared observers, with Chrome implementer input.
- [web.dev: Trust is good, observation is best (Intersection Observer v2)](https://web.dev/articles/intersectionobserver-v2): what `trackVisibility` guarantees and why `delay` must be at least 100 ms.
