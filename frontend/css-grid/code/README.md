# CSS Grid — MVP Code

The smallest runnable demo of CSS Grid: one HTML file, three grids, no dependencies and no build step. About 90 lines of actual code, comments excluded.

## What it demonstrates

- **The container owns the geometry** — a page shell built from `grid-template-areas` with `auto 1fr auto` rows, so header and footer size to their content and `main` takes the rest (`../docs/02-deep-dive.md § How`).
- **Why a bare `1fr` overflows** — two identical rows holding the same unbreakable URL, one with `1fr`, one with `minmax(0, 1fr)`. This is the automatic-minimum-size rule (spec § 6.6) made visible.
- **Responsive with zero media queries** — `repeat(auto-fit, minmax(180px, 1fr))` lets the browser pick the column count from the available width.
- **`subgrid` for cross-component alignment** — each card adopts the gallery's rows, so every price line sits on the same baseline despite titles of different heights.

## Prerequisites

Any browser released after March 2023 (Chrome 117+, Firefox 71+, Safari 16+ — `subgrid` is the constraint, not Grid itself). No Node, no npm, no server.

## Run it

```bash
# macOS / Linux
open mvp.html

# Windows (PowerShell)
start mvp.html
```

## Expected output

A dark page with three labelled sections:

1. **Shell** — a 200px-tall block: full-width header, 160px nav on the left, main filling the remainder, full-width footer.
2. **Overflow** — the first row's long URL pushes its track past the viewport and the page gains a **horizontal scrollbar**. The second row, with `minmax(0, 1fr)`, truncates the same URL with an ellipsis and stays inside the window. That scrollbar is the bug this concept prevents.
3. **Gallery** — four cards. Titles wrap to different line counts, but every card's dashed price line is on the same horizontal line, and the column count changes as you resize the window.

## What to try next

- Delete `grid-template-rows: subgrid` from `.card` and watch the price lines fall out of alignment.
- Change `auto-fit` to `auto-fill` in `.gallery`, then delete three cards — `auto-fill` keeps the column rhythm, `auto-fit` stretches one card across the full width.
- Wrap the four `<article>` elements in a plain `<div>` — placement breaks, because only *direct* children are grid items.
- Replace `grid-template-columns: 160px minmax(0, 1fr)` in `.shell` with `25% 75%` and note that `gap` is not accounted for, so the shell overflows.
- Open DevTools, find the `grid` badge next to `.gallery` in the Elements panel, and turn on the overlay to see line numbers and gaps.
