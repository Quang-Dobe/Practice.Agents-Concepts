# CSS Grid — Overview

> CSS Grid is a layout system where you draw the rows and columns of a page first, then tell each element which cells it occupies — two dimensions at once, from the parent.

## The 30-second version

Before Grid, page layout was built out of tools meant for other jobs: floats (designed for wrapping text around images) and tables (designed for tabular data). Both worked, both were miserable. Grid, shipped in every browser since 2017, lets a container declare an actual grid of tracks — `grid-template-columns: 200px 1fr 200px` — and lets children claim a rectangle inside it. The layout math (leftover space, gaps, stretching) is the browser's problem, not yours. If you have ever fought a three-column dashboard with a sticky sidebar, this is the tool that ends the fight.

## The mental model

Think of laying out a floor of an office building.

Flexbox is **seating people on a bus**. There is one aisle, one direction. You hand the driver a rule — "spread everyone out evenly", "push the last two to the back" — and passengers arrange themselves along that single line. Simple, and it adapts well when someone gets off. But if you want the person in seat 3 to also line up with someone in a different row, you are out of luck: rows do not know about each other.

Grid is **the architect's floor plan**. You draw the walls before anyone moves in: three columns, four rows, a 16px corridor between everything. Then you assign tenants: "Accounting takes the whole top strip. Engineering takes columns 1 through 2, rows 2 through 4." Tenants can span multiple cells, sit on top of each other, or be dropped in unassigned and let the building manager find them a free room (that is auto-placement).

The two big consequences of "the parent draws the plan":

- **A child's position is decided by the layout, not by its own size.** A `<div>` can start at column 2 and stretch to column 4 without knowing what is inside it.
- **Source order and visual order can differ.** You can put the sidebar last in HTML and first on screen. Useful, and a real accessibility trap — keyboard and screen-reader order still follows the HTML.

The killer unit is `fr`: one *fraction of the leftover space*. `grid-template-columns: 200px 1fr` means "200 pixels for the nav, and whatever remains for the content" — no `calc()`, no percentages that break when you add a gap.

## What it is NOT

- Not a replacement for Flexbox. Flexbox is one-dimensional (a row of buttons, a toolbar); they are used together, constantly.
- Not `display: table`. Tables size themselves from their content; a grid's tracks are declared up front.
- Not a CSS framework. Bootstrap's "grid" is a 12-column convention built from classes; CSS Grid is a browser primitive with no classes at all.
- Not a component system. It positions boxes. It says nothing about state, styling, or reuse.

## When you would reach for it

- Page-level scaffolding: header / sidebar / main / footer.
- Any layout where things must align across both rows *and* columns — dashboards, pricing tables, card galleries.
- Responsive layouts you want without media queries, via `repeat(auto-fit, minmax(240px, 1fr))`.
- Overlapping elements (a caption sitting on top of an image) without `position: absolute`.
- Nested cards whose internal rows must line up with their siblings — that is what `subgrid` is for.

## When you would NOT reach for it

- A single row or column of items with no cross-axis alignment needs — Flexbox is less code.
- Real tabular data — use a `<table>`; semantics matter more than layout convenience.
- Content-driven flow where items size themselves and you genuinely do not care about column alignment.

## Key vocabulary (just enough to keep reading)

- **Grid container** — the element with `display: grid`; it owns the plan.
- **Grid item** — a *direct* child of that container. Grandchildren are not items.
- **Track** — one row or one column.
- **Grid line** — the numbered dividers between tracks; placement is expressed in lines, not tracks.
- **Cell / area** — one square; an area is any rectangle of cells.
- **`fr`** — a share of the free space remaining after fixed sizes and gaps.
- **Explicit vs implicit grid** — tracks you declared vs tracks the browser invented because items overflowed.
- **Auto-placement** — the algorithm that finds a slot for items you did not position.
- **`minmax()` / `repeat()`** — track-sizing helpers; `minmax(200px, 1fr)` is the responsive workhorse.
- **Subgrid** — a nested grid that inherits its parent's tracks instead of defining its own (baseline in all major browsers since 2023).

*One thing still in motion: native masonry (Pinterest-style staggered columns). It is being standardized in Grid Level 3 and has shipped in Safari, but is flagged or absent elsewhere as of mid-2026 — treat it as progressive enhancement behind `@supports`.*

## What's next

The next document answers What / Where / When / How / Why in detail — the track-sizing algorithm, line-based vs area-based placement, how `fr` interacts with `min-content`, and where Grid and Flexbox draw the line against each other.
