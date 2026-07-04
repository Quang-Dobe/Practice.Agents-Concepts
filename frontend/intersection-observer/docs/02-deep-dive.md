# IntersectionObserver — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

`IntersectionObserver` is a Web API defined by the W3C Intersection Observer specification (Level 1 is a Candidate Recommendation; Level 2 adds visibility tracking). It is a browser-managed subscription mechanism that asynchronously reports the ratio of geometric intersection between a *target* element's border box and an ancestor *root* element's content box, adjusted by a CSS-style `rootMargin`. It notifies the caller only when that ratio crosses a caller-specified `threshold`, batching notifications at the end of each rendering frame.

### The core building blocks

- **`IntersectionObserver` constructor** — `new IntersectionObserver(callback, options)`. Options are `root` (Element, Document, or `null` for the top-level viewport), `rootMargin` (CSS margin string, default `"0px 0px 0px 0px"`), and `threshold` (number or number array in `[0, 1]`, default `0`). Level 2 adds `trackVisibility` (boolean) and `delay` (milliseconds, must be at least 100 when `trackVisibility` is true).
- **`observe(target)` / `unobserve(target)` / `disconnect()`** — the lifecycle. A single observer instance can watch many targets. `observe` always queues an initial callback for the target on the next rendering opportunity, even if nothing has moved.
- **`IntersectionObserverEntry`** — the immutable record delivered to the callback. Fields: `target` (Element), `time` (DOMHighResTimeStamp, relative to the time origin), `isIntersecting` (boolean), `intersectionRatio` (0 to 1), `intersectionRect` (DOMRectReadOnly of the overlap), `boundingClientRect` (target's box), `rootBounds` (root's box after `rootMargin`; `null` for cross-origin roots), and — with Level 2 — `isVisible`.
- **The intersection root** — the box the target is measured against. When `root` is `null`, this is the top-level browsing context's viewport, *not* the nearest scroll container. When `root` is a specific Element, that element must be a scrollable ancestor of the target for the observer to report anything meaningful.

### How it relates to the broader landscape

Intersection Observer belongs to the family of "browser-driven observer" APIs, alongside `MutationObserver` (DOM tree), `ResizeObserver` (element size), and `PerformanceObserver` (performance timeline entries). All four share the same design: register a callback, subscribe to targets, receive batched entries asynchronously. Its closest functional siblings are the legacy `scroll` + `getBoundingClientRect` pattern and the newer CSS Scroll-Driven Animations (`animation-timeline: view()` and `scroll()`, widely shipped in Chromium and Safari as of 2025, with Firefox implementing). Where CSS scroll-driven animations bind animation progress to scroll position on the compositor, IntersectionObserver reports discrete boolean-ish state transitions to JavaScript.

## Where

### Where it runs / lives in the stack

Purely a browser platform API. It sits at the DOM / Web Platform layer, exposed on the global `window`. There is no server-side equivalent — Node, Deno, and Bun do not implement it. It participates in the rendering pipeline: the browser computes intersection during the "update the rendering" step of the HTML event loop, after layout and before paint. The callback itself runs on the main thread, but the geometry work is done as part of the render loop the browser was going to run anyway, which is the source of its performance advantage.

### Where you typically encounter it

- **Image and iframe lazy-loading libraries** — `react-lazyload`, `lozad.js`, Vue's `v-lazy`, the internals many bundlers use before falling back to native `loading="lazy"`.
- **Analytics impression tracking** — Google Publisher Tag, ad-viewability SDKs, and product-analytics tools that need to fire an event only when an element has been in the viewport for N milliseconds.
- **Infinite-scroll implementations** — the "sentinel div" pattern in React Query, TanStack Virtual, and most feed UIs.
- **Video autoplay controls** — YouTube, Vimeo embeds pausing when scrolled out.
- **Scroll-spy navigation** — MDN's sidebar highlighting, Docusaurus, VitePress.
- **Animation-on-enter frameworks** — Framer Motion's `whileInView`, AOS (Animate On Scroll), GSAP ScrollTrigger's fallback path.

### Ecosystem and tooling

- **React wrappers**: `react-intersection-observer` (the de facto standard, provides a `useInView` hook), `@react-hook/intersection-observer`.
- **Vue / Svelte**: `@vueuse/core`'s `useIntersectionObserver`, `svelte-intersection-observer`.
- **Polyfill**: the W3C-maintained `intersection-observer` package on npm — needed only for IE11 or very old Safari. Not needed for any evergreen browser in 2026.
- **Testing**: `jsdom` does not implement it; most projects stub it with `global.IntersectionObserver = class { observe() {} unobserve() {} disconnect() {} }` or use `jest-environment-jsdom-sixteen` with a shim.

## When

### When the topic emerged and why

The API was first prototyped in Chromium in 2016 and shipped in Chrome 51 (May 2016), Firefox 55 (August 2017), Safari 12.1 (March 2019), and Edge 15 (Chromium port later brought it to all Edge versions). The motivation, spelled out in the original explainer, was that ad-viewability measurement and lazy-loading — two of the most common web-performance workloads at the time — were being implemented with `scroll` listeners that called `getBoundingClientRect` on dozens or hundreds of elements per frame. That pattern forced a synchronous layout on every scroll event, causing measurable jank on mobile and burning battery. The API is essentially the browser's way of saying "stop asking; we will tell you."

### When to use it in a project

Reach for it when:

- You need to know **whether** an element is in view, not exactly **where** it is.
- The action you take is a **state transition** (start loading, fire analytics, autoplay), not a continuous visual response to scroll.
- You are observing more than a handful of elements and a scroll listener would be O(n) per frame.
- You want the check to survive resize, zoom, and programmatic scroll without extra bookkeeping.
- You need lead-time before an element becomes visible — `rootMargin: "500px 0px"` is the single cleanest way to preload.

### When NOT to use it

Avoid it when:

- You need per-frame position (parallax, scroll-linked hero animations) — use `scroll` + `requestAnimationFrame`, or better, CSS scroll-driven animations.
- You need to know whether the element is **actually visible to a human**, not just geometrically intersecting — Level 1 does not check `opacity`, `visibility`, occlusion by sibling elements, or CSS filters. Level 2's `isVisible` covers this but only ships in Chromium.
- You need synchronous "is it visible right now?" during a click handler — the callback is asynchronous, so the freshest information is one frame stale. Fall back to `getBoundingClientRect` for a one-off check.
- You are observing shape-irregular hit areas — the spec always uses the target's axis-aligned bounding rectangle.

## How

### How it works under the hood

1. **Registration.** `observe(target)` adds the target to the observer's internal target list and queues an initial intersection computation. The target is retained by the observer, which means a leaked observer keeps its targets alive.
2. **Frame integration.** During the HTML event loop's "update the rendering" step, after style and layout are up to date and before paint, the user agent runs the *Run the Update Intersection Observations Steps* algorithm for each Document. This computes, for each `(observer, target)` pair, the intersection rectangle in root-space, the `intersectionRatio`, and which threshold band the ratio currently falls in.
3. **Threshold-crossing detection.** The observer remembers each target's previous threshold index. If the new index differs from the previous, an `IntersectionObserverEntry` is created and appended to a per-observer queue. Thresholds are always sorted ascending; the crossing check is on the *band*, not "greater than the highest threshold seen." That is why a threshold of `[0, 1]` fires twice on entry and twice on exit, not four times.
4. **Delivery.** Once per frame, the browser posts a task (spec says use `requestIdleCallback` semantics; implementations schedule it high enough to run before the next microtask checkpoint after paint) that flushes the queue by invoking the observer's callback with `(entries, observer)`. Entries from multiple targets crossing thresholds in the same frame are delivered in one call.
5. **Initial fire.** The first call after `observe()` always includes at least one entry for the newly observed target, so callers do not have to special-case the initial state. `isIntersecting` will be `false` for a target that starts outside the root.

```js
const io = new IntersectionObserver(
  (entries) => {
    for (const e of entries) {
      if (e.isIntersecting) load(e.target);
    }
  },
  { root: null, rootMargin: "200px 0px", threshold: 0 }
);
document.querySelectorAll("img[data-src]").forEach((el) => io.observe(el));
```

For one-shot use cases (lazy-load, fire-once analytics), call `io.unobserve(entry.target)` inside the callback. That is the single most common performance bug in observer code — leaking observers across route changes in SPAs.

### Key trade-offs

| Design choice | Gained | Given up |
|---|---|---|
| Asynchronous batched delivery | Zero cost on scroll; no forced layout | Cannot answer "visible now?" synchronously |
| Rectangle-only intersection | Cheap, predictable | Wrong answer for irregular or rotated shapes |
| Threshold as ratios, not pixels | Resolution- and size-independent | Must convert "when 200px is visible" into a ratio yourself |
| `root: null` means viewport, not nearest scroller | Simple for the common case | Surprising when observing inside a scrollable panel — you must pass the panel as `root` |
| `rootMargin` in CSS syntax | Familiar and expressive | Percent margins resolve against the root's size, not the target — easy to misread |
| Cross-origin `rootBounds` suppressed | Prevents viewport probing across origins | Cross-origin iframes see `null` `rootBounds` and `rootMargin` is ignored |

### Common failure modes

- **Observer leak across route changes** — component unmount doesn't call `disconnect()`; the observer keeps references to detached DOM nodes.
- **Wrong `root`** — target lives inside a scrollable panel but `root` is left as `null`; the observer only fires when the whole panel scrolls, not the panel's internal scroll.
- **Percent `rootMargin` misuse** — `"50%"` computes against the root's dimensions, not the target's. Reading it as "half the target" silently mis-triggers.
- **Threshold `1.0` on tall elements** — a target taller than the root can never reach ratio `1.0` because the intersection area is capped by the root; the callback simply never fires past `1.0`.
- **Reading `boundingClientRect` for pixel-perfect UI** — the rectangle is captured at the observation moment, up to one frame stale, and will drift under fast scroll.
- **Cross-origin iframe surprise** — `rootBounds` is `null` and `rootMargin` is ignored; code that assumes non-null `rootBounds` throws.
- **Trusting `isIntersecting` for real visibility** — an element covered by a full-screen modal with `opacity: 1` still reports `isIntersecting: true`.
- **jsdom tests failing** — no shim, `IntersectionObserver is not defined`.

## Why

### Why it exists

The API answers a first-principles problem: *scroll is the highest-frequency user input on the web, and every element that reacts to scroll pays a synchronous layout tax if it measures itself.* Browsers already know the geometry of every element as part of compositing; making JavaScript recompute it is a duplicate of work the browser was going to do anyway. Intersection Observer flips the direction of the query — the platform tells the app, not the app polling the platform — and hands the app only the deltas it asked for. That eliminates a whole class of layout-thrash bugs where reading `getBoundingClientRect` inside a scroll handler forced a style + layout recalculation that then blocked the frame the user was scrolling.

### Why it looks the way it does

An obvious alternative would have been a synchronous `element.isVisible()` API or a "visibility change" DOM event. The spec authors rejected both. A synchronous getter would either return a stale cached value (misleading) or force a layout (defeats the point). A DOM event would fire once per element with no batching and no threshold, so a page with 500 lazy images would still cost 500 event dispatches per scroll. The observer model — batch, threshold, deliver once per frame — falls out directly from those constraints. The threshold-as-ratio-array design also came from a real requirement: ad-viewability standards (IAB and MRC) define "viewable" as "50% of pixels for 1 continuous second." A ratio-based threshold expresses that directly; a pixel-based one does not, because ad slots come in many sizes.

The `null`-means-viewport quirk exists because when the API shipped, there was no way to reference the top-level viewport as an element. Passing the `<html>` element behaves differently in edge cases (frameset, printing) than the actual viewport, so `null` is a sentinel for "the real one." The cross-origin `rootBounds` suppression is a deliberate privacy choice — otherwise an embedded iframe could infer the parent page's scroll position and viewport size, which is fingerprintable.

### Why it matters now

In 2026, Intersection Observer is stable, universally supported, and still the correct tool for the state-transition class of scroll problems (lazy-load, impressions, autoplay). What is changing is the *animation* class of problems. CSS Scroll-Driven Animations (`view-timeline`, `scroll-timeline`) now cover the "animate as it scrolls in" case entirely on the compositor, with zero JavaScript and no main-thread cost. Chrome 145's scroll-triggered animations extend that to one-shot entrance animations. The pragmatic 2026 stance: keep IntersectionObserver for logic (fire an event, load data, update React state), use CSS scroll-driven animations for visuals, and stop importing scroll-animation libraries unless you need a physics simulation. That specialization makes IntersectionObserver more focused, not obsolete — the API's original use cases (analytics, lazy-loading, infinite scroll) are exactly the ones CSS cannot express because they require reacting in JS, not painting a keyframe.

## Open questions / things to verify in practice

- Does Firefox ever ship Level 2 (`trackVisibility` / `isVisible`)? As of mid-2026 it is still Chromium-only; check `caniuse.com/intersectionobserver-v2` before depending on it.
- What is the measured cost of one shared observer with 1000 targets vs. 1000 observers with one target each? The shared-observer advice is folk wisdom; measure it in your app.
- Does your framework's `useInView` clean up on Strict Mode double-mount? React 19 still runs effects twice in dev — verify no dangling observers.
- Does your observer correctly re-attach after a `display: none` toggle? Elements in `display: none` have zero-size boxes; when they return, the observer should still fire, but the initial-fire behavior depends on browser.
- Under fast programmatic scroll (e.g., `scrollTo`), does your callback see every threshold band or does the browser coalesce multiple crossings into one entry?
- When the target's ancestor has `content-visibility: auto`, do your observers behave the way you expect? That property changes what counts as "rendered" and interacts subtly with intersection.
