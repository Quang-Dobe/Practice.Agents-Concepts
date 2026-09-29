# IntersectionObserver — Overview

> `IntersectionObserver` is a browser Web API that tells you when an element enters or leaves a visible area (usually the viewport), so you don't have to measure it yourself on every scroll.

## The 30-second version

Many features depend on whether something is on screen: lazy-loading images, infinite scroll, "mark as read", ad impressions, scroll-triggered animations. The old approach listened to `scroll` events and called `getBoundingClientRect()` on each element. That runs on the main thread dozens of times per second and forces layout work. `IntersectionObserver` flips this around. You list the elements you care about, and the browser calls you back only when their visibility crosses a line you picked. The browser already computes layout and clipping while rendering, so it does the geometry work for you.

## The mental model

Picture a security guard watching a bank of CCTV monitors. The monitor frame is the **root**, usually the browser viewport. The people walking past are your **target** elements.

The scroll-listener approach is like phoning the guard every 16 ms to ask "Is Alice on camera? What about Bob?" Most of the time the answer hasn't changed, and every call interrupts the guard.

With `IntersectionObserver`, you give the guard a list and a rule: "Radio me when Alice is at least half on screen, and again when she leaves." You stop asking. The guard reports only when something crosses a line you picked, and saves up several reports to hand you together.

Two knobs shape the rule:

- **Threshold**: how much of the person must be on camera before the guard reports. `0` means "any pixel showing". `1` means "fully in frame". `[0, 0.5, 1]` means report at each of those points.
- **Root margin**: stretch or shrink the monitor frame before checking. `rootMargin: "200px"` means "tell me when Alice is within 200px of coming on camera", which is how lazy-loaders start fetching an image before you actually see it.

```js
const io = new IntersectionObserver((entries) => {
  for (const e of entries) {
    if (e.isIntersecting) loadImage(e.target);
  }
}, { rootMargin: "200px", threshold: 0 });

document.querySelectorAll("img[data-src]").forEach((img) => io.observe(img));
```

This is a simplification. The callback runs asynchronously, not the instant a pixel crosses the line. The deep dive explains what timing guarantees you do get.

## What it is NOT

- Not `ResizeObserver`. That reports when an element's **size** changes, not whether it is visible.
- Not `MutationObserver`. That reports **DOM changes** (added nodes, changed attributes).
- Not a guarantee a human saw it. An element can "intersect" while hidden behind another element or at `opacity: 0`. The experimental v2 option `trackVisibility` tries to close that gap, and support varies by browser.
- Not a scroll-position API. It reports threshold crossings, not a steady stream of scroll offsets. For effects tied to scroll position, look at CSS scroll-driven animations.

## When you would reach for it

- Lazy-loading images, iframes, or heavy components as they approach the viewport.
- Infinite scroll: watch a sentinel `<div>` at the bottom of the list and fetch the next page when it appears.
- Analytics and ad viewability: "was this card at least 50% visible?"
- Starting and pausing videos or animations based on whether they are on screen.
- Highlighting the active section in a table of contents as the reader scrolls.

## When you would NOT reach for it

- Pixel-accurate, per-frame scroll effects such as parallax. Callbacks are batched and asynchronous, so the result looks janky.
- Simple image lazy-loading where native `loading="lazy"` on `<img>` or `<iframe>` already does the job.
- Checking visibility once. A single `getBoundingClientRect()` call is simpler.
- Proving an element was not covered or tampered with, unless you accept v2's limited browser support.

## Key vocabulary (just enough to keep reading)

- **Target**: the element being watched, registered with `observe(el)`.
- **Root**: the box used as the visible area. `null` means the viewport. Otherwise it must be an ancestor of the target.
- **Threshold**: one or more ratios from 0 to 1 that trigger a callback when crossed.
- **rootMargin**: a CSS-margin-style string that grows or shrinks the root box before intersection is tested.
- **IntersectionObserverEntry**: the object passed to your callback for each change, with fields like `isIntersecting`, `intersectionRatio`, `boundingClientRect`, and `time`.
- **intersectionRatio**: the fraction of the target currently visible inside the root, from 0 to 1.
- **unobserve / disconnect**: stop watching one target, or stop watching all of them.

## What's next

The next document answers What / Where / When / How / Why in detail: how the browser computes intersections during rendering, when callbacks fire, how `root`, `rootMargin`, and `scrollMargin` interact with nested scroll containers, and why this design beats scroll listeners.
