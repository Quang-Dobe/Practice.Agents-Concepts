# CSS Grid — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

CSS Grid Layout is a **two-dimensional, track-based layout system** specified by the [CSS Grid Layout Module Level 1](https://www.w3.org/TR/css-grid-1/) (a W3C Recommendation) and extended by [Level 2](https://www.w3.org/TR/css-grid-2/) (subgrid). A box with `display: grid` or `display: inline-grid` becomes a **grid container** and establishes a new **grid formatting context** for its in-flow children, which become **grid items**. Each item is placed into a rectangular set of cells defined by row and column **tracks**, which are sized by the spec's **grid track sizing algorithm** — a constraint-propagation pass that resolves fixed, intrinsic (`min-content`, `max-content`, `auto`), and flexible (`fr`) sizes into concrete pixel values.

### The core building blocks

- **Grid container** — the element with `display: grid`. Establishes the grid formatting context. Its `writing-mode` determines the inline/block axes.
- **Explicit grid** — the tracks you declare via `grid-template-columns`, `grid-template-rows`, and `grid-template-areas`.
- **Implicit grid** — extra tracks the browser generates when items are placed outside the explicit grid, sized by `grid-auto-columns` / `grid-auto-rows`.
- **Track** — a row or column between two adjacent grid lines. Grid lines are numbered from 1 (or -1 from the end) and can carry names.
- **Grid area** — a rectangle spanning `n` cells. Named via `grid-template-areas` or shorthand `grid-area`.
- **Track sizing functions** — `<length>`, `%`, `auto`, `min-content`, `max-content`, `fit-content(x)`, `minmax(a, b)`, and the flexible `<flex>` unit `fr`.
- **Repeat notation** — `repeat(N, ...)` for fixed counts and `repeat(auto-fill|auto-fit, minmax(...))` for content-driven counts.
- **Placement properties** — `grid-column`, `grid-row`, `grid-area`, plus `grid-auto-flow` (`row`, `column`, `dense`).
- **Alignment** — `justify-*` (inline axis) and `align-*` (block axis) in three flavors: `-content` (the whole track set inside the container), `-items` (all items in their areas), `-self` (per item). Shorthand: `place-*`.
- **Subgrid** — a value of `grid-template-columns`/`rows` (Level 2) that makes a nested grid inherit its parent's track sizing and line names.

### How it relates to the broader landscape

Grid belongs to the family of **CSS formatting contexts** alongside block, inline, table, flex, and (draft) masonry / grid-lanes. Its closest sibling is **Flexbox** (`display: flex`), which is one-dimensional and content-driven — Flexbox distributes leftover space along a single axis. Grid is two-dimensional and container-driven — you design the shape, then place content. Older two-dimensional systems are `<table>` (semantic, not for layout) and the abandoned IE10 `-ms-grid` spec (different property names, no `fr` unit). The next iteration, [CSS Grid Layout Level 3](https://drafts.csswg.org/css-grid-3/), adds `display: grid-lanes` for masonry-style stacking on one axis.

## Where

### Where it runs / lives in the stack

Purely a **client-side, browser layout-engine** concern. Grid sits in the CSS layout pass — after style resolution, before paint. In Blink, LayoutNG's `NGGridLayoutAlgorithm` handles it; in WebKit, `RenderGrid`; in Gecko, `nsGridContainerFrame`. No JavaScript, no runtime, no server involvement. It participates in the same box-tree layout that produces block and flex boxes.

### Where you typically encounter it

- Design systems and component libraries (Tailwind CSS `grid-*` utilities, Radix UI primitives, MUI's `Grid2`).
- App scaffolding in Next.js / SvelteKit / Remix layouts — the top-level `layout.tsx` almost always drops into a grid.
- Editorial sites and magazine layouts (The Guardian, NYT), where uneven column spans are common.
- Dashboards and admin panels (Grafana, Retool, Superset) — data tiles arranged in named grid areas.
- Email? No. Most email clients still render fragments of CSS 2.1; Grid is unsafe there.

### Ecosystem and tooling

- **Standards and reference**: [MDN CSS Grid guides](https://developer.mozilla.org/en-US/docs/Web/CSS/CSS_grid_layout), the W3C Level 1 / Level 2 / Level 3 drafts, [Grid by Example](https://gridbyexample.com/) by Rachel Andrew.
- **DevTools**: Chrome, Firefox, and Safari each ship a Grid inspector overlay (line numbers, area names, gap highlighting). Firefox's is still the most detailed.
- **Utility CSS**: Tailwind (`grid`, `grid-cols-*`, `col-span-*`, `subgrid`), Open Props, UnoCSS.
- **Autoprefixer**: legacy `-ms-grid` translation for IE10/11 — mostly retired in 2026.
- **Frameworks**: no framework is needed. Bootstrap 5 still ships its float/flex grid but offers `.d-grid` for real Grid. CSS-in-JS libraries (Emotion, styled-components, vanilla-extract) treat Grid properties as ordinary CSS.

## When

### When the topic emerged and why

Microsoft shipped `-ms-grid` in IE10 (2011) based on an early Bo Yang / Phil Cupp draft. Rachel Andrew, Elika Etemad (fantasai), Tab Atkins, and others rewrote the spec into the modern form. It reached [Candidate Recommendation](https://www.w3.org/TR/css-grid-1/) in 2017, when Chrome 57, Firefox 52, and Safari 10.1 shipped it within weeks of each other in March 2017. Subgrid landed in Firefox 71 (2019), Safari 16 (2022), and Chrome 117 (2023), and became [Baseline widely available in March 2026](https://caniuse.com/css-subgrid).

Before Grid, two-dimensional layout meant floats plus clearfix, absolute positioning, or nested Flexbox trees. All three coupled layout intent to markup structure, which broke as soon as the design changed.

### When to use it in a project

Reach for it when:

- The design has any **cross-axis alignment** — items in row A must line up with items in row B.
- You need **named areas** to make the layout self-documenting (`"header header" "nav main" "footer footer"`).
- The layout **reshapes across breakpoints** by re-declaring tracks or areas, not by rewriting markup.
- You want the container to control **item placement** rather than each item deciding for itself.
- You need **content-aware track sizing** (`minmax(min-content, 1fr)`) that Flexbox cannot express without JavaScript.

### When NOT to use it

Avoid it when:

- You have a **single row/column** of items with even spacing — Flexbox is fewer properties and slightly faster (see § Common failure modes).
- The layout is **content order dependent** and you only need wrap-when-needed behavior — `flex-wrap` is enough.
- You're rendering **tabular data** — use `<table>`; assistive tech needs the semantics.
- You need to support **IE11 verbatim** with no fallback — the `-ms-grid` prefix implements a different algorithm (no `fr`, no auto-placement, no gaps).

## How

### How it works under the hood

The lifecycle from stylesheet to painted pixels:

1. **Container detection.** Style resolution flags the element as `display: grid`. It gets a grid formatting context. Direct children are blockified (`display: inline` becomes block-like for layout purposes) and become grid items unless they are absolutely positioned.
2. **Explicit track construction.** The engine parses `grid-template-columns`, `grid-template-rows`, and `grid-template-areas` into a track list, expanding `repeat()` and named lines. `grid-template-areas` implicitly names four lines per area (e.g. area `header` produces `header-start` and `header-end` on both axes).
3. **Item placement (auto-placement algorithm).** Items with explicit placement (`grid-column: 2 / span 2`) are placed first. Remaining items are placed by `grid-auto-flow` (default `row`) into the next available cell. `dense` backfills earlier holes at the cost of DOM order fidelity. Placement outside the explicit grid triggers **implicit tracks** sized by `grid-auto-rows`/`grid-auto-columns`.
4. **Track sizing algorithm.** Defined in [css-grid-2 § 12](https://www.w3.org/TR/css-grid-2/#algo-track-sizing). Runs per axis:
   - **Initialize** each track's base size and growth limit from its sizing function (fixed → both set; intrinsic → 0 / infinity; flexible → 0 / infinity).
   - **Resolve intrinsic sizes** by walking items in span order, distributing their `min-content` and `max-content` contributions across the tracks they span. Items that span a `fr` track are skipped in this pass.
   - **Maximize tracks** by distributing free space up to each track's growth limit.
   - **Expand flexible tracks** by computing a flex fraction — the largest value of `leftover_space / sum(flex_factors)` such that every flex track's base size at least equals its content contribution.
   - **Stretch `auto` tracks** if `align-content: stretch` (default) still leaves free space.
5. **Item layout.** Each item is laid out into its area as if the area were the containing block. `justify-self` / `align-self` decide how it fills or aligns within the area. Percentages on item sizes resolve against the area, not the container.
6. **Painting.** The grid itself paints nothing — only items and the container's own background/border are painted. `gap` is empty space; it never has a background.

Subgrid short-circuits step 2 for the nested axis: the child grid adopts its parent's line positions and names, and its items participate in the parent's intrinsic size pass. That is the whole point — cross-container alignment without duplicating track definitions.

Illustrative:

```css
.page {
  display: grid;
  grid-template-columns: [full-start] minmax(1rem, 1fr)
                         [main-start] minmax(0, 65ch)
                         [main-end]   minmax(1rem, 1fr) [full-end];
  grid-template-rows: auto 1fr auto;
  grid-template-areas:
    "header header header"
    ".      main   .     "
    "footer footer footer";
  gap: 1rem;
}
.card-list { display: grid; grid-template-columns: subgrid; grid-column: main; }
```

### Key trade-offs

| Decision | Gain | Give up |
|---|---|---|
| `fr` vs `%` | `fr` accounts for `gap` and content minimums automatically | Harder to hit an exact pixel-perfect column width |
| `auto-fill` vs `auto-fit` | `auto-fit` collapses empty tracks so items stretch to fill | `auto-fit` can produce very wide items when few items exist ([css-tricks: auto-fill vs auto-fit](https://css-tricks.com/auto-sizing-columns-css-grid-auto-fill-vs-auto-fit/)) |
| Named areas | Layout intent is legible in one glance | Areas must be rectangular; disjoint shapes need explicit line placement |
| `grid-auto-flow: dense` | No visual holes | DOM order and visual order diverge — bad for keyboard/screen reader users |
| Subgrid | Cross-container alignment without duplicating tracks | Only inherits along declared axes; still requires explicit item placement in nested grid |
| Grid over Flexbox | Two-axis alignment, container-driven design | ~10–15% slower for trivial one-axis cases, per informal benchmarks |

### Common failure modes

- **`1fr` items overflow their track** — `fr` resolves against `minmax(auto, 1fr)`, and `auto` is `min-content`. A wide `<img>` or `<pre>` blows out the track. Fix: `minmax(0, 1fr)`.
- **`auto-fit` collapses to one giant column** — when few items exist and the container is wide, empty tracks collapse and items stretch. Fix: use `auto-fill`, or cap width with `minmax(min, max)` and `justify-content: start`.
- **Percentage row heights don't work** — a `grid-template-rows: 50%` on a container without a definite height resolves to `auto` per spec. Fix: give the container a definite block size, or use `fr`.
- **Nested grids fail to align** — sibling grids each run their own sizing algorithm; columns won't line up. Fix: subgrid or hoist the tracks up one level.
- **Auto-placement of many items is O(items × tracks)** — pathological when thousands of items land in a huge implicit grid. Fix: virtualize, or place items explicitly.
- **`min-content` cliff on long words** — a single unbreakable word forces the track wider than expected. Fix: `overflow-wrap: anywhere` on the item.
- **Grid + `position: absolute` child** — an absolutely positioned grid item is placed relative to the grid area of its `grid-column`/`grid-row`, not the container. Surprising the first time.

## Why

### Why it exists

To decouple **layout structure from document structure**. HTML expresses semantics; layout is a presentational concern that should not require rearranging the DOM. Before Grid, achieving a header/sidebar/main/footer layout meant either nested wrappers (coupling markup to design) or absolute positioning (removing items from flow and losing intrinsic sizing). Grid gives you a declarative two-dimensional container without either compromise.

### Why it looks the way it does

The obvious alternative would have been to extend `<table>` or ship a JavaScript layout API. The spec authors rejected both:

- **Tables** couple layout to a specific semantic element and to a specific box model (table-cell) that interacts badly with intrinsic sizing and `box-sizing`.
- **A JS API** would run after paint, causing layout thrashing and breaking print / no-JS environments.

The `fr` unit exists because `%` was already overloaded (percentage of the parent, ambiguous with padding and border) and does not participate in `gap` accounting. `fr` is defined post-gap, post-fixed-track, and post-intrinsic-content, which is exactly what designers actually want. Named areas exist because line-number placement (`grid-column: 3 / 5`) is fragile under redesign; area names survive a re-declaration of the template.

Subgrid was postponed from Level 1 because its interaction with the track sizing algorithm required a second pass — the parent needed to know its child's content contributions to size its own tracks, which is why it took six years to reach interoperable browser support.

### Why it matters now

As of 2026, Grid is the **default layout primitive** taught in courses and shipped in design systems. Baseline widely available means teams can drop IE fallbacks. Subgrid unlocks card-based layouts (product grids, article lists) where every card's title, body, and footer align across the row — the single most-requested layout of the previous decade, previously impossible without JavaScript measurement. Grid Level 3 (`grid-lanes` / masonry) landed in [Safari 26](https://webkit.org/blog/15269/help-us-invent-masonry-layouts-for-css-grid-level-3/) and is expected in Chrome and Firefox later in 2026. Knowing where Grid ends and Flexbox begins is now table stakes for any frontend interview.

## Open questions / things to verify in practice

- Does `minmax(0, 1fr)` behave identically to `1fr` in your specific overflow case, or do you need explicit `overflow: hidden` on the item?
- How does subgrid interact with your CSS-in-JS solution — do generated class names still allow the subgrid to see parent line names?
- Benchmark: at what item count does your virtualization strategy have to kick in for `auto-fill` grids? (Measure with `performance.measure()` around the layout pass in DevTools.)
- Does your design system's spacing scale compose cleanly with `gap`, or are you double-spacing via margins?
- With RTL content, does `grid-column: 1 / 3` still mean what you think? (Answer: line numbers flip; use `grid-column-start: main-start` names for stability.)
- Where does your team draw the line between "Grid at the page level, Flex inside components" and "Grid everywhere"? Pick a convention before the codebase picks one for you.
