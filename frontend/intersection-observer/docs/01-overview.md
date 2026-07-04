# IntersectionObserver — Overview

> A browser API that tells you, asynchronously and efficiently, when an element enters or leaves the visible area of a scroll container — without you having to measure anything yourself.

## The 30-second version

Before IntersectionObserver, "is this element on screen?" was a surprisingly expensive question. You had to hook the `scroll` event, call `getBoundingClientRect()` on every candidate element, compare it against the viewport, and hope you didn't jank the main thread. IntersectionObserver flips the model: you register the elements you care about once, hand the browser a callback, and the browser notifies you — off the main thread, batched — whenever visibility crosses a threshold you defined. Engineers care because it makes lazy-loading, infinite scroll, and "in-view" analytics both correct and cheap.

## The mental model

Think of a security guard watching a bank of monitors. You don't want every teller (your components) turning around every few milliseconds to squint at the front door asking "is anyone here yet?" That's the old `scroll` + `getBoundingClientRect` world — every teller polls, constantly, on the same thread that's also serving customers.

Instead, you post a note to the guard: "Tell me the moment someone crosses this red line." The guard already watches the door as part of their job. When the line is crossed, they radio you. You keep doing your work; you only react when it matters. The "red line" is the `threshold` (how much of the element must overlap the root before firing). The "room the guard is watching" is the `root` — usually the viewport, but can be any scrollable ancestor. The "how far outside the door still counts" is `rootMargin`, which lets you fire the callback a few hundred pixels *before* the element is technically visible — perfect for preloading.

## What it is NOT

- Not a `scroll` event replacement. Scroll events tell you *the scroll position*; IntersectionObserver tells you *which elements crossed a visibility boundary*.
- Not `ResizeObserver`. ResizeObserver watches an element's own size, not its position relative to another box.
- Not `MutationObserver`. MutationObserver watches DOM tree changes (nodes, attributes), not geometry.
- Not synchronous. Callbacks fire asynchronously, so do not rely on it to read layout right before a paint.
- Not pixel-perfect for animations. It reports at threshold crossings, not every frame — use it for state transitions, not scroll-linked visuals.

## When you would reach for it

- Lazy-loading images or iframes as they approach the viewport.
- Implementing infinite scroll by observing a sentinel `<div>` at the bottom of the list.
- Firing analytics "impression" events only when an ad or card is genuinely seen.
- Pausing or autoplaying a video when it scrolls in or out of view.
- Sticky headers, scroll-spy navigation, and animate-on-enter effects.

## When you would NOT reach for it

- You need the *exact* scroll offset every frame (parallax, scroll-linked animation) — use `scroll` + `requestAnimationFrame`, or the newer scroll-driven animations CSS.
- You want to know when an element resizes — use `ResizeObserver`.
- You need to know if an element is *actually visible* to the human (not hidden by `opacity: 0`, `visibility: hidden`, or an element on top). IntersectionObserver only checks geometric intersection with the root.
- Server-side rendering paths where no DOM exists.

## Key vocabulary (just enough to keep reading)

- **root** — the box we measure against. `null` means the viewport.
- **target** — the element being observed.
- **rootMargin** — CSS-style margin that grows or shrinks the root's box before intersection is computed. Pixels or percentages only.
- **threshold** — a ratio (0.0 to 1.0), or array of ratios, at which the callback fires.
- **IntersectionObserverEntry** — the object you receive in the callback, with `isIntersecting`, `intersectionRatio`, `boundingClientRect`, and `time`.
- **observe / unobserve / disconnect** — the three lifecycle methods; always disconnect when the component unmounts.
- **isIntersecting** — the boolean shortcut most code actually branches on.

## What's next

The next document (`02-deep-dive.md`) answers What / Where / When / How / Why in detail — the exact constructor options, how thresholds interact with rootMargin, the async delivery model, browser quirks, and how it fits with React and other frameworks.
