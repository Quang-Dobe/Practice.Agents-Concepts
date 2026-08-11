# CSS Grid — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In an internal admin tool or observability dashboard, Grid is the **application shell**: header, collapsible sidebar, main scroll region, status bar. It is the one piece of CSS everyone touches and nobody wants to own, because a mistake there shifts every screen in the product at once.

In an e-commerce or content storefront, Grid owns the **product/card gallery** and increasingly the *inside* of each card. This is where Grid intersects Core Web Vitals: the gallery is usually where your Largest Contentful Paint element lives, and a badly sized track is a Cumulative Layout Shift bug reported as "the page jumps when images load."

In a design system, Grid shows up twice — once as a small set of blessed layout primitives (`<Stack>`, `<Cluster>`, `<PageShell>`), and once as raw utility classes (`grid-cols-12`, `col-span-4`) that application teams sprinkle into JSX. Which of the two dominates tells you a lot about how the codebase will age.

And in an editorial/CMS front end, Grid is what implements "centered measure with full-bleed escapes" — the article body constrained to `65ch`, with pull-quotes and hero images breaking out to the viewport edge, all from one template on the article container.

## Best practices

### 1. Write the DOM in reading order first, then lay it out
**Do:** Author the HTML in the order a screen-reader or keyboard user should encounter it, then use Grid to place it visually. Treat any divergence as a decision needing a reason.
**Why:** Tab order and screen-reader order follow the DOM, not the grid. A "just move the sidebar up on mobile" CSS tweak silently produces a keyboard trap where focus jumps from the footer back to the nav.
**Avoid:** Reordering with `order` or explicit placement purely because the visual comp reads better that way.

### 2. Use `minmax(0, 1fr)` for any track holding unknown content
**Do:** Default content-bearing columns to `minmax(0, 1fr)` and pair it with `overflow: hidden` / `text-overflow: ellipsis` or `overflow-wrap: anywhere` on the item.
**Why:** A bare `1fr` is `minmax(auto, 1fr)`. One customer with a 90-character email address, one un-wrapped log line, one image without `max-width: 100%`, and the column blows out and the whole layout gets a horizontal scrollbar in production but not in your seeded dev data.
**Avoid:** Debugging track overflow by adding `overflow: hidden` on the *container* — it hides the symptom and clips real content.

### 3. Named areas for shells, line numbers for data
**Do:** Use `grid-template-areas` for the handful of layouts with stable, nameable regions (shell, article, form). Use line-based placement (`grid-column: span 4`) for anything generated from a list.
**Why:** Named areas let you re-map the entire layout per breakpoint in three lines of ASCII, which is the single highest-leverage readability win in a shell. But an area name must exist for every region, so a 60-card gallery with named areas is unmaintainable.
**Avoid:** Mixing both in one container — reviewers can no longer tell where an item lands without running the page.

### 4. Know whether you want `auto-fit` or `auto-fill` before you type it
**Do:** `auto-fill` when a stable column rhythm matters (catalogue pages, dashboards). `auto-fit` when items should stretch to consume the row.
**Why:** With one item left after filtering, `auto-fit` collapses the empty tracks and you ship a single card that is 1400px wide. This is a real bug report, usually filed as "search results look broken."
**Avoid:** Copy-pasting `repeat(auto-fit, minmax(240px, 1fr))` everywhere; if you use `auto-fit`, cap it with `minmax(240px, 400px)` or a `justify-content: start`.

### 5. Switch templates on container width, not viewport width
**Do:** Wrap reusable layout components in `container-type: inline-size` and change `grid-template-columns` inside `@container` queries.
**Why:** The same card appears in a 320px sidebar and a 1200px main column. Viewport media queries give it the wide template in both, and you end up with per-page override classes — the exact thing that makes a design system stop being reusable.
**Avoid:** A `isCompact` React prop threaded through five components to do what one `@container` rule does.

### 6. Use `subgrid` instead of fixed heights or JS measuring
**Do:** For cards whose title/body/footer must line up across a row, make the card `display: grid; grid-template-rows: subgrid; grid-row: span 3`.
**Why:** The pre-2023 alternatives were `min-height` magic numbers (broken by translation into German) or a `ResizeObserver` loop that measures and sets heights (a reflow per frame, plus a flash of misalignment). Subgrid is Baseline widely available as of March 2026 — the fallback burden is gone.
**Avoid:** Writing `grid-template-rows: subgrid` on an element that is not a direct grid item, or on the axis you didn't span. It fails silently.

### 7. Keep track sizing functions cheap on large grids
**Do:** Prefer fixed lengths and `fr` over `auto` / `min-content` / `max-content` in grids with hundreds of items. Add `content-visibility: auto` plus `contain-intrinsic-size` on off-screen sections.
**Why:** Intrinsic track sizing requires laying out items to measure them, and items spanning multiple intrinsic tracks are resolved by span count — cost grows superlinearly with spanning items and column count. On a 2,000-row grid this is the difference between a 4ms and a 90ms layout on every resize.
**Avoid:** A virtualized list whose row template uses `auto` columns — you pay measurement cost on every scroll-driven DOM swap.

### 8. Put the grid in tokens, not in every component
**Do:** Define the gutter scale, the shell template, and the card min-width once (CSS custom properties or design-system components) and reference them.
**Why:** Otherwise `gap: 16px` / `gap: 1rem` / `gap: 15px` coexist, and a designer's "tighten the gutters" ticket becomes a 90-file PR.
**Avoid:** Hardcoding a 12-column grid because the design tool uses one — most real layouts need 2–4 named regions, not 12 anonymous ones.

### 9. Let the grid handle direction; don't hardcode left and right
**Do:** Rely on Grid's logical axes (`justify-*` is the inline axis) and name areas semantically (`nav`, `main`, `aside`) not positionally (`left`, `right`).
**Why:** Grid flips correctly for RTL locales out of the box. Area names called `left-rail` do not flip, and the mismatch surfaces as a confusing Arabic/Hebrew layout six months after launch.
**Avoid:** `direction: rtl` overrides that re-specify column order manually.

### 10. Gate genuinely new features behind `@supports`, but stop writing legacy Grid fallbacks
**Do:** `@supports (display: grid-lanes)` for masonry-style layouts; ship plain Grid and `subgrid` unconditionally.
**Why:** Grid Level 1 has been interoperable since 2017 — float fallbacks are dead weight that doubles the surface area of every layout change. Level 3 `grid-lanes` is not: it shipped in Safari 26.4 and is flagged elsewhere as of mid-2026.
**Avoid:** `@supports not (display: grid)` blocks in a 2026 codebase, and `-ms-grid-` prefixes, which are a different, incompatible spec.

## Anti-patterns to recognize

- **The wrapper div**: someone adds a `<div className="wrapper">` between the grid container and its content, usually for a click handler or a React fragment replacement. Only *direct* children are grid items, so every placement rule silently applies to one full-width wrapper instead. Use `display: contents` on the wrapper (with care — it removes the box from the a11y tree in older engines) or move the grid down a level.
- **Grid-as-table**: applying `display: grid` to a `<table>` or building a data table from `<div>`s so columns can align. It strips the table's native semantics, and re-adding `role="grid"` gets you an interactive widget contract (arrow-key navigation, focus management) you almost certainly haven't implemented. Use a real `<table>`; use `subgrid` if you need cross-row alignment inside cells.
- **Magic-number rows**: `grid-template-rows: 64px 1fr 48px` for a shell. It works until the header wraps to two lines at a narrow width or a user bumps their font size, and then content is clipped. Use `auto 1fr auto` and let the content declare its own height.
- **Dense packing on ordered content**: `grid-auto-flow: dense` to close the holes left by a spanning item. It reorders items visually while the DOM stays put, so "item 7" appears before "item 5" for sighted users and after it for everyone else. Only use `dense` when order genuinely carries no meaning (a photo mosaic, not search results).
- **Percentage tracks plus `gap`**: `grid-template-columns: 33% 33% 33%; gap: 24px`. Percentages resolve against the content box and know nothing about gaps, so the row overflows. `fr` resolves against space remaining *after* gaps — that's the whole point of the unit.
- **The `height: 100%` chain**: a grid with `1fr` rows inside a parent with indefinite height. Percentage and flexible row tracks collapse, and you get a zero-height main region. Give the container a definite height (`100dvh`, or `min-height: 0` on intermediate flex/grid items).
- **Utility-class layout sprawl**: `col-span-3 md:col-span-6 lg:col-span-4` repeated across 200 JSX files. Layout logic now lives in markup where no reviewer sees it as a whole, and a template change requires a regex. Extract the recurring shapes into named layout components.

## Real-world usage patterns

**Observability dashboard shell (B2B SaaS, tens of thousands of daily users).** Header, resizable left nav, main panel, right drawer. The grid is defined once with named areas; the nav width is a custom property (`--nav-w`) that a drag handle writes to, so resizing is a single track-size change instead of a JS-driven relayout of every panel. *Non-obvious lesson:* animating a track size in `grid-template-columns` triggers full grid layout each frame — animating the custom property with `@property` and `transition` is the same visual result but keeps the work inside the compositor's budget only if nothing inside the panel does intrinsic sizing. Test with the Performance panel, not by eye.

**Product listing page (retail, catalogue in the tens of thousands of SKUs).** `repeat(auto-fill, minmax(220px, 1fr))` for the gallery, `subgrid` inside each card so title/price/rating rows align even when one title wraps to three lines. Images carry explicit `width`/`height` (or `aspect-ratio`) so the row height is known before the image loads. *Non-obvious lesson:* the CLS win comes from the image attributes, not from Grid — Grid just stops you from needing a JS masonry library that would have made CLS worse.

**Long-form editorial site (publisher, CMS-authored body content).** The article container uses named lines: `[full-start] minmax(1rem, 1fr) [content-start] min(65ch, 100%) [content-end] minmax(1rem, 1fr) [full-end]`, every child defaults to `grid-column: content`, and a `.full-bleed` class opts into `grid-column: full`. *Non-obvious lesson:* this pattern survives arbitrary CMS output because it's opt-out by default — editors can't break the measure, they can only escape it deliberately.

**Design-system migration (platform team, ~40 product engineers).** A 12-column Flexbox framework grid replaced by three layout components backed by native Grid. The team kept both alive for two quarters behind a lint rule that blocked *new* uses of the old classes. *Non-obvious lesson:* the migration blocker was never the CSS — it was the ~200 places where the old grid's implicit `padding` on columns had been used as spacing. Grid's `gap` doesn't add padding, so every one of those sites needed a visual diff.

## Operational checklist

- **Overflow:** does every `1fr` track holding user-supplied text use `minmax(0, 1fr)` or an explicit `min-width: 0`, and has the layout been tested with a 200-character unbroken string?
- **Reading order:** does tab order match visual order at every breakpoint? Tab through the page at 360px, 768px, and 1440px — this is a 60-second check that catches most Grid a11y bugs.
- **Zoom and text scaling:** does the layout hold at 200% browser zoom and with `text-size-adjust` bumped? Fixed row heights fail here first.
- **Layout shift:** are images and embeds inside grid items given `aspect-ratio` or `width`/`height`, and is CLS monitored in RUM on the gallery/LCP page?
- **Performance:** on the largest grid in the app, what is layout time in the Performance panel on resize? Are intrinsic (`auto`, `min-content`) tracks used anywhere with more than ~100 items?
- **Internationalization:** does the layout flip correctly under `dir="rtl"`, and do the longest translated strings (German, Finnish) still fit their tracks?
- **Semantics:** is anything using `display: grid` on a `<table>`, `<ul>`, or `<dl>` in a way that changes what assistive tech reports? (`display: grid` on a `<ul>` removes list semantics in Safari/VoiceOver unless `role="list"` is restored.)
- **Ownership:** is the gutter/track scale defined in one place, and can a new engineer find the page shell's grid definition in under two minutes?
- **Progressive enhancement:** is any Level 3 feature (`grid-lanes`) behind `@supports` with a tested non-masonry fallback?

## How this topic typically evolves in a codebase

Teams almost always start with **one grid**: the page shell. It replaces a nest of flex wrappers, everyone is pleased, and Grid is considered "adopted." Nothing else changes for months because the existing components already work.

The second phase is **utility-class sprawl**. Grid utilities from Tailwind or an in-house equivalent leak into feature code, and layout decisions scatter across hundreds of components. This phase feels productive and is — until the first global design change, when you discover there is no single place that describes what the layouts are. The painful migration point is right here: consolidating scattered utilities into a small set of named layout primitives, which is tedious because every consolidation is a visual diff that only a human can approve.

The third phase is **component-scoped layout**: container queries plus `subgrid`, where a card decides its own template from its own width and inherits alignment from its gallery. This is where the codebase stops needing layout props and per-page overrides. Teams that reach it usually got there by first paying the consolidation cost — you cannot bolt container queries onto layout logic that lives in 200 JSX files. Budget for that consolidation before it becomes urgent.

## Further reading

- [Best Practices With CSS Grid Layout](https://www.smashingmagazine.com/2018/04/best-practices-grid-layout/) — Rachel Andrew, who edited the spec, on the questions that actually come up: areas vs lines, when to nest, when not to use Grid at all.
- [The Dark Side of the Grid, Part 1](https://matuzo.at/blog/the-dark-side-of-the-grid) and [Part 2](https://matuzo.at/blog/the-dark-side-of-the-grid-part-2) — Manuel Matuzovic on Grid's accessibility failure modes; Part 2 is the definitive treatment of source order vs visual order.
- [Grid layout and accessibility](https://developer.mozilla.org/en-US/docs/Web/CSS/CSS_grid_layout/Grid_layout_and_accessibility) — MDN's short, authoritative version of the same problem, including the current state of `reading-flow`.
- [CSS Grid Layout Module Level 2](https://drafts.csswg.org/css-grid-2/) — read §6.6 (automatic minimum size) and §12 (track sizing) once; they explain nearly every Grid bug you will ever file.
- [gridbugs](https://github.com/rachelandrew/gridbugs) — a curated list of engine-specific Grid bugs with test cases. Check it before assuming your layout is wrong.
- [Help us invent CSS Grid Level 3, aka "Masonry" layout](https://webkit.org/blog/15269/help-us-invent-masonry-layouts-for-css-grid-level-3/) — WebKit's argument for why masonry belongs in Grid, useful background before you adopt `grid-lanes`.
