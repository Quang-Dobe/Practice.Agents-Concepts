# CSS Grid — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

CSS Grid Layout is a two-dimensional, line-based layout model specified in the [CSS Grid Layout Module](https://drafts.csswg.org/css-grid-2/). An element with `display: grid` becomes a **grid container** and establishes an independent *grid formatting context*. Its in-flow direct children become **grid items**, each occupying a rectangular **grid area** bounded by four **grid lines** (row-start, column-start, row-end, column-end).

Sizes are computed by the **grid sizing algorithm** (§ 11–12 of the spec), which resolves column tracks and row tracks in separate passes over a set of *track sizing functions*. Placement of unpositioned items is computed by the **auto-placement algorithm** (§ 8.5). Both are deterministic and specified step-by-step — this is not heuristic layout.

The module is versioned:

- **Level 1** — the core model. Shipped in Chrome 57 / Firefox 52 / Safari 10.1 (March 2017), Edge 16 (October 2017).
- **Level 2** — adds `subgrid`. Firefox 71 (2019), Safari 16 (2022), Chrome/Edge 117 (September 2023). Baseline "widely available" since March 2026.
- **Level 3** — adds masonry-style layout, now specified as `display: grid-lanes`. Shipped in Safari 26.4; behind a flag elsewhere as of mid-2026.

### The core building blocks

- **Explicit grid** — tracks you declare with `grid-template-columns` / `grid-template-rows` / `grid-template-areas`.
- **Implicit grid** — tracks the browser generates when an item lands outside the explicit grid. Sized by `grid-auto-rows` / `grid-auto-columns` (default `auto`).
- **Track sizing function** — a `<track-size>`: a length, a percentage, `min-content`, `max-content`, `auto`, `fit-content(x)`, `minmax(min, max)`, or a flexible `<flex>` (`1fr`).
- **`fr`, the flexible length** — a share of the *leftover* space after non-flexible tracks and gaps are resolved. Critically, a bare `1fr` means `minmax(auto, 1fr)`, not `minmax(0, 1fr)`. That one detail causes most grid overflow bugs.
- **Placement properties** — `grid-row` / `grid-column` (line-based, supports `span N` and negative indices like `-1` for the last line), or `grid-area` referencing a name from `grid-template-areas`.
- **`repeat()` with `auto-fill` / `auto-fit`** — repetition count computed from available space. `auto-fill` keeps empty tracks; `auto-fit` collapses them to `0` so remaining items stretch.
- **Gutters** — `row-gap` / `column-gap` / `gap`. Gaps are consumed *before* `fr` distribution, so percentages never have to account for them.
- **Alignment** — `justify-*` on the inline axis, `align-*` on the block axis; `*-items` positions items in their areas, `*-content` positions the whole track set inside the container, `*-self` overrides per item.
- **`subgrid`** — a nested grid whose tracks in one or both axes are adopted from the parent's spanned area rather than defined locally.

### How it relates to the broader landscape

Grid belongs to the family of CSS *formatting contexts* alongside block, inline, table, flex, and (newer) masonry. The dividing line against Flexbox is dimensionality: Flexbox sizes items along a **main axis** from content outward, Grid sizes **tracks** from the container inward, then fits items into them. Tables size from content and cannot overlap cells; absolute positioning gives you overlap but no automatic sizing. Grid is the only one of the four that gives you declared track geometry, two-axis alignment, overlap, and auto-placement at once.

## Where

### Where it runs / lives in the stack

Entirely in the browser's **layout stage** of the rendering pipeline — after style resolution, before paint and compositing. In Chromium it is implemented in Blink's LayoutNG engine (`layout/grid/`), in Gecko as `nsGridContainerFrame`, in WebKit as `RenderGrid`. There is no runtime, no JavaScript, and no network cost. A grid layout invalidates and recomputes on container resize, font load, or content change — which is exactly where its performance characteristics bite.

### Where you typically encounter it

- **Application shells** — VS Code's web build, Grafana dashboards, admin panels: header / sidebar / main / status bar as named areas.
- **Design systems** — Tailwind's `grid-cols-*`, `col-span-*`, `auto-rows-*` utilities are thin wrappers over Grid; Bootstrap 5 ships a real CSS Grid mode alongside its Flexbox 12-column system.
- **Editorial layouts** — the "full-bleed inside a centered column" pattern using named lines (`[content-start] minmax(0, 65ch) [content-end]`).
- **Card galleries** — `repeat(auto-fit, minmax(240px, 1fr))` for responsive tiles without media queries.
- **Card *internals*** — `subgrid` so every card's title, body, and footer row line up across the gallery even with uneven content.
- **Email and print** — mostly *not*. Grid support in email clients is patchy; print engines vary.

### Ecosystem and tooling

- **For debugging** — Chrome and Edge DevTools' Grid overlay (line numbers, area names, gap shading); Firefox's Grid Inspector is still the most complete, including the "extend lines infinitely" and track-size readout.
- **For authoring** — Tailwind CSS grid utilities; CSS Modules / vanilla-extract for scoped `grid-template-areas`; `@container` queries to switch templates on component width instead of viewport width.
- **For compatibility checking** — [caniuse.com/css-grid](https://caniuse.com/css-grid), MDN's Baseline badges, and Rachel Andrew's [gridbugs](https://github.com/rachelandrew/gridbugs) list of known engine-specific bugs.
- **For accessibility** — the `reading-flow` / `reading-order` properties (Chrome 137, May 2025) let you decouple focus order from DOM order for grid and flex containers, which is the first real fix for Grid's source-order problem.
- **Standards to read** — `css-grid-2` (current), `css-grid-3` (grid-lanes), `css-align-3` (alignment), `css-box-alignment` for `gap`.

## When

### When the topic emerged and why

Grid started at Microsoft. IE10 shipped a `-ms-grid-` prefixed implementation in 2012, derived from Silverlight/XAML's `Grid` panel. The W3C spec was first published in 2011, matured for six years, then landed near-simultaneously in Chrome, Firefox, and Safari in **March 2017** — an unusually coordinated launch, driven by browser vendors deliberately holding back until interop was there.

The motivating problem: for two decades page layout was built from `float` (specified for wrapping text around images), `display: table` (specified for tabular data), and `position: absolute` (no auto-sizing). None of them expressed "columns of this size, rows of that size, this box spans two of each." Frameworks papered over it with percentage-width `float` classes and clearfix hacks, which broke the moment you added padding or a gutter. Flexbox (2012–2015) solved the one-dimensional case and got used for the two-dimensional case anyway, badly — nested flex rows that could not align across each other.

### When to use it in a project

Reach for Grid when:

- The layout has **alignment requirements across both axes** — rows and columns must line up simultaneously.
- The container should **own the geometry** and children should not need to know their own width.
- You want **responsive behavior without media queries** via `auto-fit` + `minmax()`.
- Elements must **overlap** (image + caption, badge over card) and you still want automatic sizing.
- You need **source order independent of visual order** for a specific reason — and you are prepared to handle focus order.
- Nested components must **share the parent's track geometry** (`subgrid`).

### When NOT to use it

Avoid Grid when:

- The layout is a **single line** of items — a toolbar, a button row, a tag list. Flexbox is fewer declarations and wraps more naturally.
- The data is **actually tabular**. Use `<table>`; `display: grid` on a table strips its accessibility semantics unless you re-add ARIA roles, which is a maintenance trap.
- Items must **size themselves and wrap based on their own content width** with no cross-item alignment. That is exactly Flexbox's `flex-wrap`.
- You are **reordering content visually for aesthetics** rather than structure — keyboard and screen-reader order still follows the DOM, and `reading-flow` is not yet cross-browser.
- You are targeting **email clients** or legacy print pipelines.

## How

### How it works under the hood

Grid layout is a two-pass process: place first, then size. Sizing runs the same algorithm twice — once for columns, once for rows — because row sizes usually depend on column widths (text wraps), not the reverse.

**Phase 1 — placement (§ 8.5).** The auto-placement algorithm runs in four steps:

1. Position anything with a **definite row and column** position.
2. Process items **locked to a row** (in `grid-auto-flow: row`), growing the implicit grid as needed.
3. Determine the **number of columns** in the implicit grid.
4. Sweep the remaining items with a **placement cursor**. In default *sparse* mode the cursor never moves backwards, so a wide item can leave a hole. `grid-auto-flow: dense` resets the cursor and backfills holes — which is why `dense` visually reorders items and is an accessibility hazard.

**Phase 2 — track sizing (§ 12), per axis:**

1. **Initialize track sizes** — each track gets a base size (from its min function) and a growth limit (from its max function). `fr` tracks start with an infinite growth limit.
2. **Resolve intrinsic track sizes** — items are laid out to find their min-content and max-content contributions. Items spanning multiple intrinsic tracks are handled by *distributing extra space across spanned tracks* (§ 12.5.1), smallest span count first.
3. **Maximize tracks** — grow base sizes toward growth limits using free space.
4. **Expand flexible tracks** — compute a *hypothetical fr size* and multiply by each flex factor. If the total flex factor is under 1, tracks get only their fraction of free space, not all of it.
5. **Stretch `auto` tracks** — if `align-content`/`justify-content` is `normal` or `stretch`, remaining space is split equally among `auto`-max tracks.

```
       1        2        3        4      ← column lines
       ├─200px──┼──1fr───┼──1fr───┤
   1 ──┌────────┬────────┬────────┐
       │  nav   │     header      │      header: grid-column: 2 / 4
   2 ──├────────┼────────┼────────┤
       │        │  main  │ aside  │
   3 ──└────────┴────────┴────────┘
        line -4  ...      line -1         negative indices count from the end
```

**The automatic minimum size rule (§ 6.6)** sits underneath all of this: a grid item in a track with an `auto` minimum and non-flexible max gets `min-width: auto`, meaning *at least its min-content size*. A long unbreakable string or a wide `<img>` therefore forces its column wider than `1fr` implies. This is the single most common surprise in Grid.

### Key trade-offs

| Choice | You gain | You give up |
|---|---|---|
| `grid-template-areas` vs line numbers | Readable, self-documenting layout; trivial to re-map per breakpoint | Only rectangular areas; every area needs a name; awkward for dozens of items |
| `1fr` vs `minmax(0, 1fr)` | `1fr` never clips content | `1fr` lets wide content blow the track out; `minmax(0,1fr)` needs `overflow`/`text-overflow` handling |
| `auto-fit` vs `auto-fill` | `auto-fit` stretches items to fill the row | `auto-fill` keeps a stable column rhythm; `auto-fit` makes a single item full-width |
| `dense` packing vs sparse | No holes, tighter visual result | Visual order diverges from DOM order — bad for keyboard users |
| Intrinsic tracks (`auto`, `min-content`) vs fixed/`fr` | Content-driven sizing, no magic numbers | Measurably slower; cost scales with item count |
| Subgrid vs re-declaring tracks | True cross-component alignment | Deeper layout dependency chains; more relayout on change |
| Grid vs Flexbox for the same job | Two-axis control, no wrapper divs | More verbose for one-dimensional cases |

### Common failure modes

- **Column blows past its `1fr` width.** A long URL, `<pre>`, or unsized image sets a min-content floor via `min-width: auto`. Fix: `minmax(0, 1fr)` or `min-width: 0` on the item.
- **Grid ignores your grandchildren.** Only direct children are grid items. A wrapper `<div>` between container and content silently breaks placement.
- **Rows collapse to zero on a `height: 100%` container.** Percentage row tracks resolve against an indefinite height and behave as `auto`.
- **`auto-fit` gives one giant card.** With a single item, all other tracks collapse to `0` and the item stretches full width. Use `auto-fill`, or cap with `minmax(240px, 400px)`.
- **Focus order jumps around.** `order`, `dense`, or explicit placement moved things visually; the tab sequence still follows the DOM. Fix the DOM, or use `reading-flow: grid-order` where supported.
- **Layout gets slow at scale.** Items spanning intrinsic tracks must be measured against every candidate position, and the CSSWG has flagged this as quadratic in both spanning-item count and column count ([csswg-drafts #10266](https://lists.w3.org/Archives/Public/public-css-archive/2024May/0011.html)). Igalia's measurements found `auto` tracks roughly 60% slower than stretched tracks, with the gap widening as the grid grows. Fix: fixed or `fr` tracks, plus `content-visibility: auto` on off-screen sections.
- **Subgrid renders but nothing aligns.** `subgrid` only inherits the axis where you wrote the keyword, and only across the area the subgrid item actually spans.

## Why

### Why it exists

Layout is a constraint-satisfaction problem: given a container of unknown size and content of unknown size, assign positions such that alignment, spacing, and overflow rules hold. Before Grid, CSS had no vocabulary for expressing those constraints in two dimensions, so authors encoded them imperatively — in percentages, in JavaScript resize handlers, in nested wrapper elements whose only job was to create an axis. Grid moves the constraint solving into the engine, where it can run on every reflow at native speed and respond to font loads, zoom, and container resize without an author-written listener.

### Why it looks the way it does

The non-obvious decision is **line-based placement rather than cell-based**. It would have been simpler to say "this item is in cell (2,3)" — that is how HTML tables and most JS grid libraries work. Lines were chosen because they make spanning, negative indexing, and `subgrid` fall out for free: `grid-column: 1 / -1` means "first line to last line" regardless of how many tracks exist, so a layout stays correct when a track is added. A cell-coordinate model would require every span to be re-expressed when the track count changes.

The second decision is **`fr` as a flexible length rather than a percentage**. Percentages resolve against the container's content box and therefore double-count gaps; `fr` resolves against *free space after gaps and fixed tracks*, so `grid-template-columns: 200px 1fr; gap: 16px` is exact with no `calc()`. The cost is that `fr` is not a plain length — it participates in a separate expansion phase and cannot be used inside `calc()`.

The third is **`fr` defaulting to `minmax(auto, 1fr)`**. The alternative, a `0` minimum, would silently clip content and make overflow the default. The committee chose "never lose content by default" and accepted that authors would occasionally need `minmax(0, 1fr)`. It is a defensible trade, and it is why that snippet is in every senior frontend engineer's muscle memory.

### Why it matters now

Grid is stable infrastructure, not a trend, but three things changed recently enough to be worth re-learning. **Subgrid** reached Baseline "widely available" in March 2026, so cross-component alignment is finally safe to ship without fallbacks. **`reading-flow` / `reading-order`** (Chrome 137) begins closing the ten-year-old accessibility gap between visual and DOM order — watch for Firefox and Safari. And **`display: grid-lanes`** in [CSS Grid Level 3](https://drafts.csswg.org/css-grid-3/) shipped in Safari 26.4, ending the long masonry debate; it reuses `fr`, `gap`, `auto-fill`, `minmax()`, spanning, and `subgrid`, and adds `flow-tolerance` to control how aggressively items chase the shortest lane. Treat it as progressive enhancement behind `@supports (display: grid-lanes)` until Chromium ships unflagged.

Combined with container queries, Grid is also what makes genuinely component-scoped layout possible: a card can pick its own template based on its own width, with no viewport media query anywhere in the chain.

## Open questions / things to verify in practice

- Measure it: build a 2,000-item grid with `auto` rows versus fixed rows and compare layout time in DevTools' Performance panel. Does the ~60% figure hold on current Blink?
- Does `minmax(0, 1fr)` change anything visible in a layout with no overflowing content, or is it a free default I should always write?
- How does `subgrid` behave when the subgrid item spans fewer tracks than it has children? Does it overflow, or generate implicit tracks?
- Test `reading-flow: grid-order` with an actual screen reader (NVDA, VoiceOver) — does the announced order match the visual order, or only the tab order?
- Compare `repeat(auto-fit, minmax(240px, 1fr))` against a container-query-driven template on the same component. Which produces better breakpoints for a card that appears in both a sidebar and a full-width page?
- Confirm the current `grid-lanes` flag status in Chrome Canary and Firefox Nightly before assuming the mid-2026 picture still holds.
