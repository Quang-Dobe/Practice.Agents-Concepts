# IntersectionObserver — MVP Code

The smallest runnable demo of `IntersectionObserver`. About 60 lines of actual code, comments excluded.

## What it demonstrates

- **One shared observer, many targets** — every card and the lazy panel are watched by a single `IntersectionObserver` instance (Best Practice #1 from `03-practice.md`).
- **Threshold array unlocks bands** — `threshold: [0, 0.5, 1]` is what makes the three-state label (OFF SCREEN / PARTIALLY VISIBLE / FULLY VISIBLE) expressible at all (Best Practice #4).
- **One-shot `unobserve` inside the callback** — the dashed panel loads its content on first intersection, then removes itself from the observer's target list (Best Practice #2).
- **`isIntersecting`, not `ratio > 0`** — the label function branches on the boolean first, matching the spec's edge-case behavior.

## Prerequisites

Any modern browser (Chrome 51+, Firefox 55+, Safari 12.1+). Python 3 for the local static server. No npm install, no bundler.

## Run it

```bash
# From this directory:
python3 -m http.server 8000
# then open http://localhost:8000/mvp.html
```

`mvp.ts` is the same logic in strict TypeScript, kept for reference. Compile it with `tsc --target ES2022 --module ESNext mvp.ts` if you want to swap it in for the inline script.

## Expected output

The sticky black log at the top shows the newest five callback entries, e.g.:

```
card-2  ratio=1.00  in=true
card-2  ratio=0.55  in=true
card-3  ratio=0.02  in=true
card-1  ratio=0.00  in=false
card-3  ratio=0.00  in=false
```

Each card's inline label flips between the three states as you scroll, and its "seen N time(s)" counter increments only on OFF -> ON transitions. Scroll past the dashed panel: it turns green and reports the load time; scroll back — the log no longer mentions it.

## What to try next

- Change `threshold: [0, 0.5, 1]` to just `0` and observe that the "PARTIALLY / FULLY" distinction collapses to a single OFF/ON flip.
- Set `rootMargin: '200px 0px'` and watch cards flip to visible before they cross the fold — that is the lazy-load preload trick.
- Delete the `observer.unobserve(lazyEl)` line, scroll past the panel repeatedly, and see the log spam its id forever.
- Open DevTools -> Rendering -> "Intersection observer overlay" to see the observer's rects drawn on the page as you scroll.
