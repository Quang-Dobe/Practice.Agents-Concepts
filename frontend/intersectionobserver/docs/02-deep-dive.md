# IntersectionObserver — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

`IntersectionObserver` is a W3C Web API ([Intersection Observer spec](https://w3c.github.io/IntersectionObserver/), Editor's Draft, last revised March 2026) that lets script register interest in the geometric intersection between a **target** element and an **intersection root** (an ancestor element, a `Document`, or the implicit top-level viewport). The browser computes the intersection during the rendering phase of the event loop. When the intersection crosses one of the configured **thresholds**, or when `isIntersecting` flips, the browser queues an `IntersectionObserverEntry` and later delivers a batch of entries to the callback in a normal task. The API is **asynchronous, batched, and threshold-driven**. It is not continuous and it is not synchronous.

### The core building blocks

- **`IntersectionObserver(callback, options)`**: the constructor. Options are `root`, `rootMargin`, `scrollMargin`, `threshold`, and the v2 options `delay` and `trackVisibility`. An invalid margin string throws `SyntaxError`. A threshold outside `[0, 1]` throws `RangeError`.
- **Intersection root**: `null` means the *implicit root*, which is the top-level browsing context's viewport, even from inside an iframe. An explicit `Element` or `Document` root must be an ancestor of the target in the containing-block chain. If it is not, the target never intersects.
- **Root intersection rectangle**: the root's content box (or the viewport), grown or shrunk by `rootMargin`. `rootMargin` accepts `px` and `%` only. Percentages resolve against the root's size.
- **`scrollMargin`**: grows or shrinks the clip rect of every scroll container between the target and the root. `rootMargin` only moves the root's edge. `scrollMargin` also moves the edges of nested scrollers such as carousels.
- **Thresholds**: a sorted list of ratios. The browser tracks which "bucket" the current ratio falls into (the *threshold index*) and notifies on bucket changes.
- **`IntersectionObserverEntry`**: a snapshot with `time`, `rootBounds`, `boundingClientRect`, `intersectionRect`, `intersectionRatio`, `isIntersecting`, `isVisible` (v2), and `target`.
- **Registration**: each target keeps an internal list of `(observer, previousThresholdIndex, previousIsIntersecting)` records. On `observe()`, `previousThresholdIndex` starts at `-1`. The first computation therefore always differs from the stored state, so every new target gets one initial entry, even if it is off screen.
- **Control methods**: `observe(el)`, `unobserve(el)`, `disconnect()`, and `takeRecords()`, which synchronously drains queued entries that have not been delivered yet.

### How it relates to the broader landscape

IntersectionObserver belongs to the browser's **observer family**, which all share the "register once, get batched async callbacks" shape. `MutationObserver` reports DOM tree changes and delivers them as microtasks. `ResizeObserver` reports box-size changes during rendering. `PerformanceObserver` reports timing entries. IntersectionObserver reports *geometry relative to an ancestor*. Its closest functional neighbours are not observers. They are declarative features built on the same machinery: `loading="lazy"`, `content-visibility: auto`, and CSS scroll-driven animations.

## Where

### Where it runs / lives in the stack

It runs in the browser, on the client, on the **main thread** of the renderer. Geometry is computed inside the rendering pipeline, after style and layout, using the same clip and scroll data the compositor uses. The callback is ordinary JavaScript on the main thread. MDN explicitly warns to keep it short and to defer heavy work, for example with `requestIdleCallback`. The API does not exist on the server. SSR frameworks must guard it behind a client-only lifecycle hook.

### Where you typically encounter it

- **Lazy loading**: Chromium implements native `loading="lazy"` on top of its internal intersection-observer machinery, and libraries like lazysizes and lozad use the public API.
- **Infinite scroll / feeds**: sentinel elements in React (`react-intersection-observer`), TanStack Virtual demos, and Twitter/X-style timelines.
- **Ad viewability and analytics**: the MRC viewability standard ("50% of pixels visible for 1 continuous second" for display ads) maps directly onto `threshold: 0.5` plus a timer. Google Publisher Tag and most analytics SDKs use it for impression tracking.
- **Framework primitives**: Angular's `@defer (on viewport)`, Astro's `client:visible` islands, and Next.js `<Link>` viewport prefetching are all IntersectionObserver under the hood.
- **Docs sites**: scroll-spy tables of contents in Docusaurus, VitePress, and MDN itself.

### Ecosystem and tooling

- **For framework integration**: `react-intersection-observer` (`useInView`), VueUse `useIntersectionObserver`, Svelte actions, Angular `@defer`.
- **For declarative alternatives**: `loading="lazy"` on `<img>`/`<iframe>`, `content-visibility: auto`, CSS `animation-timeline: view()`.
- **For legacy support**: the W3C `intersection-observer` polyfill on npm. It is effectively obsolete, because the API has been Baseline since March 2019 when Safari 12.1 shipped it.
- **For testing**: jsdom does not implement it, so Jest/Vitest need a mock. Playwright and Cypress run real browsers and exercise it natively.
- **For conformance**: web-platform-tests `intersection-observer/` directory. Interop 2024/2025 proposals tracked v2 visibility work.

## When

### When the topic emerged and why

Before 2016, visibility detection meant `scroll` + `resize` listeners calling `getBoundingClientRect()` or `offsetTop` per element. Each call can force synchronous layout, and cross-origin iframes could not see their own position at all. Ad networks hacked around that with timing side channels such as `requestAnimationFrame` rates and Flash tricks. Google proposed IntersectionObserver to give ads and lazy loaders a cheap, privacy-preserving answer. It shipped in Chrome 51 (2016), then Edge 15 and Firefox 55 (2017), then Safari 12.1 (March 2019). Chromium shipped IntersectionObserver v2 (`trackVisibility`, `delay`, `isVisible`) in Chrome 74 (April 2019) as an anti-clickjacking and ad-fraud measure. Firefox and Safari still do not implement v2. `scrollMargin` is the newest addition: Chrome 120 (December 2023), Firefox 141, and Safari 26.0, which made it Baseline in September 2025.

### When to use it in a project

Reach for it when:

- You need to react to **coarse visibility transitions** (entered, left, crossed 50%) for many elements.
- Work should start *before* the user reaches content. `rootMargin: "300px"` prefetches one screen ahead.
- The target may live in a **cross-origin iframe** and needs to know whether it is on screen, which scroll math cannot tell it.
- You are building a framework primitive (defer, hydrate-on-visible, prefetch-on-visible) that must stay cheap when there are thousands of targets.

### When NOT to use it

Avoid it when:

- You need **per-frame scroll position**, for example parallax or progress bars. Use CSS scroll-driven animations or a passive `scroll` listener with `requestAnimationFrame`.
- Native `loading="lazy"` covers the case. It is less code and the browser tunes its distance thresholds per network type.
- You only need a **one-off check**. One `getBoundingClientRect()` call is simpler than constructing an observer.
- You need **proof of human visibility** across all browsers. v2 is Chromium-only, and v1 ignores occlusion, opacity, and `visibility`.
- You need to **skip rendering** off-screen content. `content-visibility: auto` does this without script.

## How

### How it works under the hood

The browser computes intersections once per rendering opportunity (typically once per frame, around 60 Hz, and throttled in background tabs), not on every scroll event.

1. **Register**: `observe(target)` adds a registration record with `previousThresholdIndex = -1` and `previousIsIntersecting = false`, and marks the document as having observations to run.
2. **Render step**: during the event loop's *update the rendering*, after `requestAnimationFrame` callbacks and ResizeObserver delivery and before paint, the HTML Standard runs the **update intersection observations steps** for each document.
3. **Compute root intersection rectangle**: take the root's content box or the viewport and apply `rootMargin`. For targets in a cross-origin-domain frame observed against the implicit root, `rootMargin` and `scrollMargin` are **ignored** and `rootBounds` is `null`, so the frame cannot probe the embedder's geometry.
4. **Compute intersection**: start from the target's border box. Walk up the containing-block chain, clipping by each ancestor that has `overflow` clipping or `clip-path`. Each scrollport is expanded by `scrollMargin`. At an iframe boundary, clip to that frame's viewport and continue in the parent document. Finally, intersect with the root intersection rectangle.
5. **Derive state**: `intersectionRatio = area(intersectionRect) / area(boundingClientRect)`. A zero-area target counts as ratio `1` if it intersects. `isIntersecting` is true if the rectangles overlap **or are edge-adjacent**, even with zero area. The threshold index is the position of the first threshold greater than the ratio.
6. **Compare**: if the threshold index or `isIntersecting` differs from the stored values, create an entry, append it to the observer's `[[QueuedEntries]]`, and update the stored state. v2 also skips the target if less than `delay` ms has passed since its last notification.
7. **Deliver**: *queue an intersection observer task* on the IntersectionObserver task source. At most one such task is pending per document. When the task runs, each observer with queued entries gets its callback called once, with all its entries as an array.

```
scroll / layout change
        |
        v
[update the rendering] --rAF--> ResizeObserver --> IO compute (geometry) --> paint
                                                       |
                                                 state changed?
                                                       | yes
                                                       v
                                           [[QueuedEntries]] += entry
                                                       |
                                    queue task (IO task source, once/doc)
                                                       v
                                  callback(entries[], observer)  // later task
```

The consequence: your callback sees the state **as of the last rendered frame**, one task later. It never runs mid-scroll-event, and it never forces layout. The entry's `boundingClientRect` was captured during rendering, so reading it is free. Calling `target.getBoundingClientRect()` yourself is not.

### Key trade-offs

| Design choice | Gained | Given up |
|---|---|---|
| Sampled once per frame | Cost bounded by frame rate, not scroll event rate | Fast flings can skip a target entirely: it enters and exits between two samples and no entry is queued |
| Threshold buckets instead of a continuous ratio | Few callbacks, predictable load | No smooth progress value. Emulating one with `threshold: [0, 0.01, ..., 1]` gives up most of the savings |
| Async task delivery | Never blocks rendering, batches naturally | At least one frame of latency. Not suitable for synchronous layout decisions |
| Geometry-only (v1) | Cheap. Reuses layout and clip data | Ignores opacity, `visibility: hidden`, z-order occlusion, and transforms that hide content |
| v2 `trackVisibility` | Strong "unoccluded, unfiltered" guarantee | Expensive hit testing. `delay` is forced to at least 100 ms. Chromium-only. Conservative: any non-2D transform or opacity below 1 gives `false` |
| `rootMargin` ignored cross-origin | Embedder geometry does not leak to third-party frames | Lazy-loading inside cross-origin embeds cannot prefetch ahead with the implicit root |
| One observer, many targets | One options object, shared batching | All targets must share one root, margin, and threshold. Different configs need separate observers |

### Common failure modes

- **`threshold: 1` never fires**: the target is taller than the root, so the ratio caps below 1. Or fractional pixels under zoom and transforms produce 0.9999.
- **Initial callback treated as "entered"**: every `observe()` produces an entry immediately, often with `isIntersecting: false`. Code that ignores the flag loads everything at once.
- **Lazy images in a horizontal carousel load late**: `rootMargin` only grows the root. The carousel's own `overflow` still clips at 0 px. Fix with `scrollMargin`, or with the carousel as `root`.
- **Explicit `root` that is not an ancestor**: no entry ever reports intersecting, and nothing throws.
- **Embedded widget ignores `rootMargin`**: the target sits in a cross-origin iframe with the implicit root, and the spec discards margins there.
- **Infinite scroll stalls**: the sentinel stays intersecting after a page loads and fills less than one screen. No state change occurs, so no new callback fires. Re-check after appending, or `unobserve`/`observe` the sentinel.
- **Memory growth in SPAs**: observers are never `disconnect()`ed on unmount, so detached subtrees stay registered.
- **Tests crash with `IntersectionObserver is not defined`**: jsdom lacks the API, so unit tests need a mock.
- **Heavy callback causes jank**: synchronous fetch setup, DOM writes, and forced layout inside a callback that handles hundreds of entries. Batch the work and yield.

## Why

### Why it exists

It addresses three first-principles problems. **Cost**: layout reads inside scroll handlers cause forced synchronous layout and main-thread contention exactly when the user wants smooth scrolling. **Information boundaries**: a cross-origin iframe cannot legally learn where it sits on the page, but ads and embeds need to know whether they are on screen. **Separation of concerns**: the renderer already knows every element's clipped geometry, so asking the app to recompute it duplicates work at a worse time.

### Why it looks the way it does

The obvious alternative was a **synchronous `isVisible(el)` query**, or a `visibilitychange`-style event fired inline with scroll. A synchronous query forces layout on demand, which recreates the thrash problem. An inline event couples script to the scroll hot path and, with async scrolling on the compositor thread, cannot even be accurate. The spec instead piggybacks on work the renderer already does (layout and clip computation during *update the rendering*) and hands back a precomputed snapshot one task later. Thresholds are there to reduce notifications: the browser tracks bucket changes, so a list of targets costs roughly one comparison per target per frame instead of one callback per pixel. Ignoring `rootMargin` in cross-origin frames looks like an odd special case. It follows from the privacy goal: a margin would let a frame binary-search the embedder's layout. The v2 visibility check is conservative, and says `false` whenever it cannot prove visibility, because its intended consumers (click-jacking defence, ad fraud) need a guarantee rather than a best guess.

### Why it matters now

In 2026 the API is stable and widely used. It sits under much of the declarative web: framework viewport triggers (Angular `@defer`, Astro islands, Next.js prefetch) and native lazy loading. The growth is at the edges. `scrollMargin` reached Baseline in September 2025 and fixes the long-standing nested-scroller gap. CSS scroll-driven animations (Chromium 115+, Safari 26) are taking over the "animate on scroll" cases that people used to force onto IntersectionObserver. v2 visibility is still Chromium-only, and interop work on it is ongoing. For a working engineer, the skill that matters is knowing its sampling and timing model well enough to choose between IO, CSS, and native attributes, not the syntax.

## Open questions / things to verify in practice

- How often does a fast trackpad fling or `scrollTo({behavior: "instant"})` skip a small target entirely, and does adding `rootMargin` hide the gap?
- What is the real per-frame cost of observing 5,000 targets with one observer versus 50 observers, measured in the Performance panel?
- Does `scrollMargin` behave the same across Chrome 120+, Firefox 141+, and Safari 26 for nested horizontal scrollers inside a vertical page?
- How does `entry.time` relate to `performance.now()` inside the callback? That gap is the actual delivery latency.
- In Chrome, what does `isVisible` report for a target under a `position: fixed` header, at `opacity: 0.99`, or with a `transform: rotate(1deg)`?
- Does the sentinel-stall scenario reproduce in my infinite-scroll setup, and which re-arm strategy (`takeRecords`, unobserve/observe, or a manual check) is cleanest?
