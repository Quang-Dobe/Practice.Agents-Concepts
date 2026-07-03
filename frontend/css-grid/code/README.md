# CSS Grid — MVP Code

The smallest runnable demo of CSS Grid. One self-contained HTML file, three panels, about 30 lines of actual CSS.

## What it demonstrates

- **Panel 1** — `fr` units share leftover space in a `1fr : 2fr : 1fr` ratio, and `gap` is subtracted *before* `fr` resolves. See `../docs/02-deep-dive.md § How`.
- **Panel 2** — `grid-template-areas` expresses a header / nav / main / aside / footer holy-grail shell as an ASCII drawing. See `../docs/02-deep-dive.md § Key trade-offs`.
- **Panel 3** — `repeat(auto-fit, minmax(min(14rem, 100%), 1fr))` builds a responsive card grid with no `@media` block. See `../docs/03-practice.md § Best practice 7`.

## Prerequisites

Any modern browser (Chrome, Firefox, Safari, or Edge from 2017+). No Node, no build step, no dependencies.

## Run it

```bash
# From the code/ folder, either open it directly:
xdg-open mvp.html      # Linux
open mvp.html          # macOS
start mvp.html         # Windows

# Or serve it (useful if you want file:// restrictions gone):
python3 -m http.server 8000
# then browse to http://localhost:8000/mvp.html
```

## Expected output

A single page with three stacked panels. Panel 1 shows three boxes with the middle one twice as wide. Panel 2 shows a five-region page shell with a full-width header and footer sandwiching a three-column middle row. Panel 3 shows six cards that reflow from many columns down to one as you narrow the window.

For maximum insight, open DevTools and click the small `grid` badge next to each `.panel-*` container in the Elements panel — the browser overlays track lines, gaps, and area names.

## What to try next

- Change Panel 1's template to `1fr 2fr 1fr` versus `100px 2fr 100px` and shrink the window — watch fixed tracks refuse to give.
- In Panel 2, swap the `grid-template-areas` string to move `aside` to the left of `main`. No markup changes.
- In Panel 3, replace `auto-fit` with `auto-fill` and delete four cards — the empty tracks now stay reserved instead of collapsing.
- Add `grid-auto-flow: dense` to Panel 3 and span one card across two columns via `grid-column: span 2` — see items backfill holes (and reflect on why that hurts keyboard users).
