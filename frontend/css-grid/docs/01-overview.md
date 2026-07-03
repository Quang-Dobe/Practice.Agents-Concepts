# CSS Grid — Overview

> CSS Grid is the browser's native way to lay out a page as a two-dimensional grid of rows and columns, so you describe the shape of the layout once and let the browser place the children into it.

## The 30-second version

CSS Grid is a layout system built into every modern browser since 2017. You mark an element as `display: grid`, tell it how many rows and columns you want and how wide each should be, and its direct children snap into that grid. Before Grid, building a page with a header, a sidebar, a main content area, and a footer meant nested `<div>`s, floats, absolute positioning, or a Flexbox tree three levels deep. With Grid, that whole layout is a handful of CSS properties on a single parent.

## The mental model

Think of a spreadsheet. When you open Excel, you get a fixed matrix of rows and columns before you type anything. Cells have coordinates (A1, B3), you can merge them, and you drop content into whichever cell you want. The row/column structure exists independently of the content.

CSS Grid is that spreadsheet, but for HTML. You tell one parent element: "give me three columns — 200 pixels, then flexible, then 200 pixels — and two rows." That parent now has six cells arranged in space. Each child element you drop inside can either flow into the next open cell automatically, or you can say "this child spans column 1 to column 3, row 2 to row 3" and it lands exactly there.

The important shift: with Flexbox and older techniques, you arrange items along **one** axis at a time and hope the wrapping works out. With Grid, you design the **container's shape** first, then place items into it. The layout drives the content, not the other way around.

## What it is NOT

- Not Flexbox. Flexbox aligns items along a single axis (a row or a column). Grid aligns them along both at once.
- Not a `<table>`. Tables are for tabular data with semantic meaning. Grid is presentation only and works on any elements.
- Not Bootstrap's 12-column grid. That's a CSS framework built on floats/flex; native Grid needs no framework.
- Not a replacement for the whole layout toolkit. You'll still use Flexbox inside grid cells, and Grid inside flex items.

## When you would reach for it

- Overall page scaffolding: header, sidebar, main, footer in one shot.
- Any layout where rows and columns need to align across sections (product cards, dashboards, form fields).
- Magazine-style layouts where items span uneven numbers of columns or rows.
- Responsive layouts that reshape at breakpoints without changing HTML — a three-column grid becomes one column on mobile with one line of CSS.

## When you would NOT reach for it

- Laying out a single row of buttons in a toolbar. That's Flexbox, one line.
- Content that flows purely vertically with no cross-axis alignment. Regular block flow is fine.
- Tabular data. Use `<table>` — screen readers depend on the semantics.
- Supporting Internet Explorer 11 without a fallback. Its old Grid spec is a different language.

## Key vocabulary (just enough to keep reading)

- **Grid container** — the element with `display: grid`. Its direct children become grid items.
- **Grid item** — a direct child of the container. It occupies one or more cells.
- **Track** — a single row or column in the grid.
- **Grid line** — the boundary between tracks, numbered from 1.
- **Cell** — the intersection of one row and one column.
- **Grid area** — a rectangle of one or more cells, optionally given a name.
- **`fr` unit** — "fraction of the leftover space." `1fr 2fr` splits remaining width 1:2.
- **`grid-template-columns` / `grid-template-rows`** — where you declare the tracks.
- **`gap`** — spacing between tracks. Replaces the old margin-hack.
- **Implicit grid** — extra rows/columns the browser adds when items overflow your declared tracks.

## What's next

The next document, `02-deep-dive.md`, answers What / Where / When / How / Why in detail — including track sizing functions, named areas, `auto-fit` vs `auto-fill`, subgrid, and how the browser resolves item placement when you leave things unspecified.
