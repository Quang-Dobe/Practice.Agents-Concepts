# IntersectionObserver — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In a typical SaaS dashboard, IntersectionObserver is the thing that lets a 200-row table scroll smoothly while each row lazy-loads its avatar, and it is what tells the analytics client that a metric card was actually seen before you attribute a click to it. It is nearly always working somewhere below the framework layer, wrapped in a `useInView`, `v-intersect`, or `use:inview` action.

In an e-commerce feed, it is the sentinel at the bottom of the product grid driving infinite scroll, and it is what tells the video hero to pause when the user scrolls past. In a docs site, it is the scroll-spy powering the sidebar's "current section" highlight. In an ads product it is the ratio-and-time gate implementing the IAB 50%-for-1-second viewability rule.

If you are reading a modern frontend PR that mentions "sentinel", "impressions", "lazy-hydrate", or "whileInView", IntersectionObserver is somewhere in the diff — usually as an implementation detail of a hook the product engineer never touches directly.

## Best practices

### 1. Share one observer across many targets
**Do:** Instantiate a single `IntersectionObserver` per *concern* (lazy-load-images, log-impressions, autoplay-videos) and call `observe()` on every target for that concern.
**Why:** Each observer allocates internal state and adds a step to the browser's per-frame intersection loop. One observer with 500 targets is one intersection pass; 500 observers with one target each is 500 passes, plus 500 callback dispatches.
**Avoid:** Creating a fresh `new IntersectionObserver` inside each list item component. That is the single most common perf regression in feed UIs.

### 2. `unobserve` inside the callback for one-shot triggers
**Do:** For lazy-load, first-impression, and enter-animation cases, call `observer.unobserve(entry.target)` the moment `entry.isIntersecting` becomes true and the work has been dispatched.
**Why:** Keeps the observer's target list small as the user scrolls a long feed, and prevents the same analytics event from firing twice when the element re-enters view.
**Avoid:** Leaving the target observed forever and guarding with a `Set<Element>` of "already fired" — you keep paying intersection cost for elements you no longer care about.

### 3. Use `rootMargin` to preload before the element is visible
**Do:** For lazy images and prefetched routes, set `rootMargin: "200px 0px"` (or larger on fast networks) so the callback fires before the element crosses the fold.
**Why:** By the time the user actually sees the row, the image or route bundle is already in cache. Zero-margin lazy-loading looks correct in demos and produces a visible pop-in shimmer on real devices.
**Avoid:** Copying `"50%"` from a blog post without checking — percent margins resolve against the *root*'s dimensions, not the target's, so `50%` on a full-viewport root is half a screen.

### 4. Use a threshold array for ramps, a single value for triggers
**Do:** Use `threshold: 0` (or a small number like `0.01`) for "in view yet?" and `threshold: [0, 0.25, 0.5, 0.75, 1]` when you want a scrub-like animation opacity ramp.
**Why:** Each extra threshold value adds a per-frame comparison, but that cost is trivial compared to running a scroll listener. What matters is that a single-value threshold cannot express a ramp — you get one callback on entry and one on exit, and everything in between is dark.
**Avoid:** Cranking `threshold` to `[0.00, 0.01, 0.02, ... 1.0]` (101 values) — you now pay callback overhead every frame while the element is scrolling and you have reinvented the scroll listener.

### 5. Buffer the sentinel for fast-scroll infinite feeds
**Do:** Place the sentinel `<div>` well above the last item (e.g. 4–8 rows up) *and* apply a generous `rootMargin` like `"800px 0px"`.
**Why:** On a trackpad flick or programmatic `scrollTo`, the browser can skip past a zero-margin sentinel between two frames and the observer never sees the crossing — the feed stalls until the user nudges again. A buffered sentinel triggers the next fetch while the user still has content to read.
**Avoid:** Putting the sentinel *as* the last item and relying on the network to keep up. That is where "why did my infinite scroll dead-stop at row 40?" bugs come from.

### 6. Always tear down on unmount
**Do:** In React, call `observer.disconnect()` in the effect cleanup. In Vue, call it in `onBeforeUnmount`. In vanilla, tie the observer's lifetime to the same signal that removes the elements.
**Why:** An observer holds strong references to its targets; a leaked observer keeps detached DOM alive and, worse, invokes your callback with stale entries pointing at nodes that no longer exist. Callbacks calling `setState` on unmounted components produce the classic React warning.
**Avoid:** Forgetting the cleanup in a custom hook and shipping it to 50 components. You will only find out from a memory-leak flame graph after a bug report.

### 7. Depend on stable identities in effect deps
**Do:** In a React `useEffect` that creates an observer, list only *stable* dependencies (root ref, threshold constant). If the callback needs fresh state, put the state in a ref and read `ref.current` inside the callback.
**Why:** Recreating the observer on every render tears down and rebuilds the platform-level subscription each keystroke; on top of the perf cost, the *initial fire* re-emits for every target, which double-counts impressions.
**Avoid:** Passing an inline callback as an effect dep, then wondering why analytics show every card was seen twice.

### 8. Pass the right `root` when scrolling inside a container
**Do:** If the observed element lives inside a `<div style="overflow: auto">`, pass that div as `root`. Do not leave `root: null`.
**Why:** `root: null` measures against the *viewport*, not the nearest scroller. The observer will fire when the panel itself scrolls into view, not when the target scrolls within the panel — a silent no-op that looks correct until QA tries the app inside a modal.
**Avoid:** The "why doesn't lazy-load work inside my drawer?" ticket. Always check what actually scrolls.

### 9. Keep callbacks cheap; defer heavy work
**Do:** Inside the callback, do only what is needed to *decide* — set a boolean, push to a queue, schedule the actual work with `requestIdleCallback` or a microtask.
**Why:** Callbacks run on the main thread as part of the render loop's tail. A long callback delays paint of the very frame that just scrolled, which shows up as jank in the exact moment the user is looking.
**Avoid:** Decoding a large image, parsing JSON, or calling a synchronous layout-forcing API inside the callback.

### 10. Do not use it for actual-visibility gating on important paths
**Do:** For analytics or ad viewability where "was the user really shown this?" matters, use Level 2's `trackVisibility: true` and `delay: 100` where available, and fall back to a page-visibility + focus check.
**Why:** Level 1 reports geometric intersection only — a fullscreen modal, `opacity: 0`, or a `filter: blur()` overlay all still count as "intersecting." Naive impression counts inflate.
**Avoid:** Shipping an ad-viewability metric that only checks `isIntersecting` and then defending it to a client whose CPM depends on it.

## Anti-patterns to recognize

- **The observer-per-item mistake**: each list item mounts its own observer. It looks clean in a component, but scales linearly and re-subscribes on every keystroke that re-renders the list. Hoist a shared observer to the list parent (or a module-level singleton) and pass `observe`/`unobserve` down.
- **The forgotten `disconnect`**: the effect body creates the observer, the cleanup returns nothing. Detached DOM lingers and callbacks fire after unmount. Always return `() => observer.disconnect()`.
- **The single-threshold ramp**: someone wants a fade-in that follows scroll and sets `threshold: 0.5`. Element pops from 0% to 100% opacity at the halfway mark. Use a threshold *array* or, better, a CSS scroll-driven animation.
- **The rootMargin sign confusion**: positive margins *expand* the root (fire early), negative margins *shrink* it (fire late). People write `"-100px"` intending "fire 100px early" and get the opposite. Remember CSS box-model semantics.
- **The synchronous assumption**: code reads `entry.boundingClientRect` inside a click handler that just called `observe()`. The callback has not run yet. Use `getBoundingClientRect` for one-off synchronous reads.
- **The re-created-every-render observer**: React component creates the observer in the render body or in an effect with unstable deps. Every render tears it down and rebuilds it — every rebuild re-fires the initial entry, so analytics double-count.
- **The `intersectionRatio: 0, isIntersecting: true` panic**: at the exact edge crossing, spec says `isIntersecting` is true even when the ratio is `0`. Branch on `isIntersecting`, not `intersectionRatio > 0`.
- **The polyfill-in-2026 waste**: shipping the W3C polyfill for a target audience of modern browsers. It has not been needed for evergreen browsers since 2020; drop it.

## Real-world usage patterns

- **Infinite feed at newsroom scale.** A public news site renders 20 story cards per page, prepends a sentinel `<div>` 6 cards from the bottom, and observes it with `rootMargin: "1000px 0px"` against the article-list scroller. Non-obvious lesson: the sentinel needs a non-zero height (even 1px) — Safari has historically skipped intersection computation for zero-height boxes inside flex containers.

- **Deferred hydration in an SSR marketing site.** The page ships static HTML; below-the-fold widgets are wrapped in a `<lazy-hydrate>` custom element that observes itself with `rootMargin: "50%"` and, on first intersection, dynamically imports its client bundle and hydrates. Non-obvious lesson: use one module-level observer for *all* lazy-hydrate boundaries — do not let each custom element instantiate its own or the observer count explodes in a long landing page.

- **Video autoplay/pause in a social feed.** A short-video feed observes every `<video>` with `threshold: [0, 0.75]`. Crossing `0.75` calls `play()`, crossing back below `0.75` calls `pause()`. Non-obvious lesson: browsers block `play()` when the tab is backgrounded; combine with the Page Visibility API so you do not queue up a play promise that rejects.

- **Ad-viewability logging.** A publisher SDK observes each ad slot with `threshold: 0.5` and a `trackVisibility: true, delay: 100` Level-2 observer. A 1-second timer starts on entry, cancels on exit; only a completed timer logs an impression. Non-obvious lesson: use `entry.time` (a `DOMHighResTimeStamp`) rather than `Date.now()` — it is monotonic and immune to system-clock jumps.

- **Sticky-header pinning detection.** A sentinel `<div style="height: 1px">` is placed immediately above the sticky header. The observer's `root` is the scroll container and `threshold: 0`. When the sentinel *leaves* the root, the header is pinned; add a `.is-pinned` class. Non-obvious lesson: this trick avoids the ancient hack of listening to `scroll` and comparing offsets — and it works for horizontally sticky headers just as cleanly.

## Framework integration sketch

A reusable React hook worth memorizing:

```tsx
function useInView<T extends Element>(opts?: IntersectionObserverInit) {
  const ref = useRef<T | null>(null);
  const [inView, setInView] = useState(false);
  useEffect(() => {
    const node = ref.current;
    if (!node) return;
    const io = new IntersectionObserver(
      ([entry]) => setInView(entry.isIntersecting),
      opts,
    );
    io.observe(node);
    return () => io.disconnect();
  }, [opts?.root, opts?.rootMargin, opts?.threshold]);
  return [ref, inView] as const;
}
```

The two things new engineers get wrong: forgetting the cleanup, and passing `opts` itself as the dep (a fresh object each render). Destructure the primitive fields into the dep array as above, or memoize the options object at the call site.

## Operational checklist

- Monitoring: is there a metric tracking observer count and impression event volume per page? Runaway growth signals a leak or double-fire.
- Failure handling: is there a code path for browsers where `IntersectionObserver` is undefined (only relevant if you support IE/legacy WebViews)?
- Testing: is there a jsdom shim in the test setup with a `triggerIntersection(target, isIntersecting)` helper so component tests can exercise the callback?
- Cleanup: does every observer creation site have a paired teardown that runs in the test-rendered lifecycle?
- Cost: is the callback allocation-free on hot paths (no per-call arrow functions passed into `.map`, no `setState` on every crossing when a ref would do)?
- Security: for third-party embeds, are you aware that cross-origin roots produce `null` `rootBounds`? Any code that dereferences it must null-check.
- Onboarding: can a new engineer point at the *shared* observer for lazy-loading and explain why it is not per-item?
- Behavior: is there a Chrome DevTools "Rendering > Intersection observer overlay" screenshot in the PR when the change touches thresholds or `rootMargin`?

## How this topic typically evolves in a codebase

Teams usually start with a copy-pasted `useInView` in one component, then a second, then a third with slightly different options. Six months in, someone notices that scrolling a long list is janky and finds five observers per list item; a refactor consolidates them into a shared context provider that exposes `observe(el, cb)` and `unobserve(el)`. This is the standard pattern for mature apps — a single module-level observer per concern, thin wrappers per component.

The painful migration point is going from "each component owns its observer" to "the app owns the observer and components subscribe." It touches every list, every card, every ad slot; do it before the analytics team starts complaining about double-counted impressions, not after.

The 2026 evolution is deciding what still belongs in IO versus in CSS scroll-driven animations. Visuals (fade-in, parallax) migrate out; logic (fetch next page, log impression, hydrate widget) stays. The clean end-state has IO doing *only* what CSS cannot: run JavaScript in response to a visibility state change.

## Further reading

- [W3C Intersection Observer specification](https://www.w3.org/TR/intersection-observer/) — the source of truth for the update algorithm, threshold semantics, and cross-origin rules.
- [MDN: Intersection Observer API](https://developer.mozilla.org/docs/Web/API/Intersection_Observer_API) — the reference every practitioner keeps open; note the "Timing" section that explains the initial-fire guarantee.
- [Google Web.dev: Lazy-loading with IntersectionObserver](https://web.dev/browser-level-image-lazy-loading/) — canonical worked examples for images and iframes, with the native `loading="lazy"` fallback discussion.
- [`react-intersection-observer` README](https://github.com/thebuilder/react-intersection-observer) — read the source, not just the API; it is the reference implementation for a well-behaved React wrapper (cleanup, StrictMode, options memoization).
- [Surma: Scroll-triggered animations](https://surma.dev/things/scroll-animations/) — the essay that popularized "use CSS for visuals, IO for logic," with concrete numbers.
- [Rick Byers: Intersection Observer V2 explainer](https://github.com/w3c/IntersectionObserver/blob/main/explainer-v2.md) — motivates `trackVisibility` and explains why Level 2 costs more to compute than Level 1.
