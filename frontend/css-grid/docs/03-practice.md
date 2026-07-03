# CSS Grid — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In a typical SaaS frontend, Grid is what owns the outermost page shell — the header/sidebar/main/footer skeleton that every route renders inside. Next.js `app/layout.tsx`, SvelteKit `+layout.svelte`, Remix `root.tsx` — nine times out of ten, that top-level container is a two-line `display: grid` with named areas. Everything inside is Flex or block flow.

The second place Grid shows up is any list-of-cards view: product listings, dashboards, article grids, admin tables that aren't actually `<table>`s. That's where `repeat(auto-fit, minmax(...))` earns its keep — the same CSS renders a 4-up desktop layout and a 1-up mobile layout with no JavaScript and no media queries.

The third habitat is design-system primitives. Radix, MUI, shadcn/ui, and internal component libraries all ship a `<Grid>` or `<Stack orientation="grid">` primitive that wraps Grid semantics behind typed props. If you're building or contributing to such a library, you'll spend real time deciding which Grid features to expose and which to hide.

Editorial and dashboard products (Guardian-style layouts, Grafana-style tile boards, Notion-style databases) push Grid the hardest, because every row must align with every other row across independently rendered cards. That's where subgrid stops being a nice-to-have.

## Best practices

### 1. Grid for the page, Flex for the component
**Do:** Reach for Grid at layout boundaries where two-axis alignment matters (page shell, card grids, form matrices). Reach for Flex inside a card or toolbar where you have one row/column of siblings.
**Why:** Grid at the leaf level buries layout intent inside components that get reused in unpredictable containers. Flex at the page level ends up as five nested wrappers because Flex is one-dimensional.
**Avoid:** `display: grid` on every wrapper "for consistency." You will re-nest Grid inside Grid three levels deep and lose all alignment across siblings.

### 2. Use `minmax(0, 1fr)` when content can overflow
**Do:** Write `grid-template-columns: minmax(0, 1fr) minmax(0, 1fr)` whenever a track contains user text, long URLs, code blocks, or images without an explicit width.
**Why:** Plain `1fr` resolves to `minmax(auto, 1fr)`, and `auto` is `min-content`. A single unbreakable word or a wide `<pre>` will push the track wider than the container, breaking your grid and often introducing horizontal scroll on the page.
**Avoid:** Adding `overflow: hidden` on the item as a fix — it hides the symptom, not the cause, and clips valid content.

### 3. `auto-fill` vs `auto-fit`: pick on purpose
**Do:** Use `auto-fill` when you want a stable column count even when items are missing (form grids, filterable card lists with variable results). Use `auto-fit` when you want items to expand to consume the row (hero cards, empty-state layouts).
**Why:** `auto-fit` collapses empty tracks — with three cards in a five-column layout, each card becomes 1/3 of the container. That looks great sometimes and enormous the rest of the time.
**Avoid:** Cargo-culting `auto-fit` everywhere because a blog post recommended it. The failure mode (one card stretched across a 1600px screen) is ugly and specific.

### 4. Name your areas when the layout has meaning
**Do:** Use `grid-template-areas` for page shells and dashboard scaffolds. The template block reads like an ASCII drawing of the layout.
**Why:** Named areas survive redesign. Changing "sidebar on the left" to "sidebar on the right" is a one-line swap in the template string; with numeric line placement, every item's `grid-column` needs to change.
**Avoid:** Using areas for card lists where the shape is `repeat(auto-fill, ...)`. Areas require a fixed rectangle; they don't compose with content-driven track counts.

### 5. Use subgrid for card-internal alignment
**Do:** When a card has header/body/footer that must line up with siblings across the row, make the card `display: grid; grid-template-rows: subgrid` spanning three rows of the parent.
**Why:** Before subgrid, aligning three rows of independently-sized card sections meant either a JavaScript ResizeObserver dance or forcing fixed heights on every section. Subgrid does it declaratively.
**Avoid:** Faking it with `min-height: 4rem` on every title. It works until someone translates the app to German.

### 6. Prefer `gap` over margins
**Do:** Use `gap` (or `row-gap` / `column-gap`) for spacing between grid items. Reserve margins for spacing between the container and its parent.
**Why:** `gap` participates in the track sizing algorithm — `fr` units account for it. Margins do not, and they collapse in surprising ways next to `fr` tracks. `gap` also applies only between items, not on the outer edges, which is almost always what you want.
**Avoid:** `grid-template-columns: repeat(3, 1fr); .card { margin-right: 1rem }` — the last card overflows and the first has an uneven left edge.

### 7. Design responsive without media queries when you can
**Do:** `grid-template-columns: repeat(auto-fill, minmax(min(20rem, 100%), 1fr))` gives you a fully responsive card grid: many columns on desktop, one column on mobile, no `@media` block.
**Why:** Media-query-driven layouts are brittle across container widths (a sidebar collapse suddenly triggers "mobile" for a desktop user). Intrinsic responsive layouts adapt to whatever space they actually get, which is essential inside container queries and split-pane apps.
**Avoid:** Hardcoding breakpoints (`@media (min-width: 768px)`) for layouts that could express themselves in one line of `minmax`.

### 8. Don't let visual order fight DOM order
**Do:** Keep tab order and reading order aligned with the DOM. If a design places a "Buy" button visually first but conceptually last, either reorder the DOM or accept that Grid's `order` / `grid-row` reorder is a **visual-only** change that screen readers and keyboards will not follow.
**Why:** [WCAG 1.3.2 (Meaningful Sequence)](https://www.w3.org/WAI/WCAG21/Understanding/meaningful-sequence.html) fails when Grid reorders content that carries meaning. `grid-auto-flow: dense` is the most dangerous knob here — it silently rearranges items to fill visual holes.
**Avoid:** `dense` on any layout containing focusable elements.

### 9. Add `min-width: 0` to any grid item that contains scrollable or wrappable content
**Do:** For flex-child or grid-child items that wrap long text or contain a scrolling `<pre>` / `<table>`, set `min-width: 0` explicitly. Same for `min-height: 0` on the block axis.
**Why:** The default `min-width: auto` on grid items resolves to their min-content contribution. That's the same overflow trap as best practice 2, one layer down.
**Avoid:** Debugging phantom horizontal scrollbars for an hour before remembering this rule.

### 10. Verify with the DevTools grid overlay every time you write template-columns
**Do:** Open Chrome or Firefox DevTools, click the `grid` badge next to your container in the Elements panel, and turn on line numbers and area names. Firefox's overlay shows track sizes in pixels and highlights `gap` distinctly.
**Why:** Grid errors are almost always silently wrong rather than loudly broken. A missing `1fr` produces a subtly narrower column, not an exception. The overlay is the fastest way to catch that.
**Avoid:** Debugging Grid layouts by inspecting the DOM tree alone. The whole point of Grid is that layout isn't in the DOM.

### 11. Prefer intrinsic sizing over pixel tracks
**Do:** Reach for `auto`, `min-content`, `max-content`, and `fit-content()` before hardcoding pixel widths. `grid-template-columns: max-content 1fr auto` covers most real dashboards.
**Why:** Fixed pixel tracks assume a specific font, language, and zoom level. Translated apps and users with larger font sizes will overflow or clip. Intrinsic tracks adapt to actual content.
**Avoid:** `grid-template-columns: 240px 1fr 120px` in an internationalized app — the day someone translates the labels to Finnish is the day the layout breaks.

### 12. Do not put Grid in a hot animation path
**Do:** Confine Grid to layout that changes on route transition, viewport resize, or user interaction — not per frame. Animate transforms and opacity on individual items instead.
**Why:** Changing `grid-template-columns` triggers a full layout pass on the container and all descendants. Chrome's LayoutNG made this cheaper, but it is still not a 60fps operation on complex grids.
**Avoid:** JavaScript that mutates `grid-template-columns` on `scroll` or `mousemove`. Wrap the affected item in `transform` instead.

## Anti-patterns to recognize

- **Pixel-only track lists.** `grid-template-columns: 200px 200px 200px 200px` looks explicit and safe. It breaks the moment a card contains more text than expected or the browser zoom hits 150%. Replace with `repeat(auto-fill, minmax(12rem, 1fr))`.
- **Hardcoded row counts.** `grid-template-rows: repeat(3, auto)` implies exactly three items. Add a fourth and it lands in an implicit row with different sizing. Leave rows implicit and use `grid-auto-rows` if you must constrain them.
- **Grid used as one-dimensional Flex.** A row of buttons in `display: grid; grid-template-columns: repeat(4, 1fr)` is Flex with a longer property name. Flex composes better with variable item counts and `flex-wrap`.
- **Ignoring intrinsic sizing.** Setting `width: 100%` on every grid child fights the sizing algorithm. Items already fill their area unless you tell them not to.
- **`grid-auto-flow: dense` on interactive content.** It fills holes visually while leaving tab order in DOM order, producing a keyboard trap that jumps around the screen.
- **Percentage row heights on auto-height containers.** `grid-template-rows: 50% 50%` on a body without a fixed height silently becomes `auto auto` because percentages resolve against an indefinite block size.
- **Reaching for JavaScript ResizeObserver to align cards.** If the layout is "make every card's title/body/footer line up across the row," subgrid replaces the entire ResizeObserver + `element.style.height =` mess.
- **Nested grids for card internals when one grid would do.** Every nested grid is a new sizing pass and a new alignment context. Hoist tracks up to the highest common ancestor when possible; use subgrid when it isn't.

## Real-world usage patterns

**E-commerce product listing.** A retail site renders 20–60 product cards per category page. The parent is `display: grid; grid-template-columns: repeat(auto-fill, minmax(min(16rem, 100%), 1fr)); gap: 1.5rem`. Each card is `display: grid; grid-template-rows: subgrid; grid-row: span 4` so image, title, price, and CTA line up across every row regardless of title length. Lesson: subgrid removes an entire class of "one card is taller than the others" bugs that used to require ResizeObserver.

**Editorial homepage.** A news site's front page uses a 12-column grid with named lines. Feature stories span 8 columns, secondary stories 4, and the layout changes shape by breakpoint via a small set of media queries that reassign `grid-column` on labeled items. Lesson: name your columns (`[content-start]`, `[feature-end]`) so responsive re-declarations don't rely on brittle numeric indexes.

**SaaS dashboard.** A monitoring UI arranges tiles (chart, metric, alert list) in a two-column grid with `grid-template-areas`. Users can rearrange tiles via drag-and-drop; the drop handler updates the `areas` string, not the DOM order. Lesson: Grid is the only layout system that lets you reorder content visually without touching the DOM, which keeps focus, selection, and scroll position intact during interaction.

**Form-heavy admin panel.** Complex forms use `grid-template-columns: max-content 1fr` at the fieldset level so labels auto-size to their longest string and inputs share the remaining space. `column-gap: 1rem; row-gap: 0.5rem` handles spacing. Lesson: `max-content` on a label column is the CSS translation of "make it as wide as it needs to be, no more" — Flex cannot express this without JavaScript.

## Operational checklist

- **Overflow guard:** Have you added `minmax(0, 1fr)` or `min-width: 0` anywhere long text, images, or code blocks might live?
- **Accessibility:** Does tab order match visual order? Is `grid-auto-flow: dense` used only in decorative contexts?
- **RTL correctness:** Does the layout use logical property names (`grid-column-start: content-start`) rather than numeric line indexes that flip under RTL?
- **Fallback for very old browsers:** If your traffic still includes IE11 (rare in 2026 — verify), do you have a Flex or block fallback? Otherwise, drop the fallback code — it's dead weight.
- **DevTools verified:** Has someone opened the Grid inspector on the layout and confirmed track sizes and gap match the design?
- **Layout shift budget:** Does the layout hold its shape during font loading and image loading? Explicit `aspect-ratio` on image tiles prevents CLS.
- **Container query readiness:** Does the layout depend on viewport media queries where a container query would fit the component-composition model better?
- **Print styles:** Does the layout collapse sensibly under `@media print`? Grid tracks with `1fr` become brittle at print widths.
- **Onboarding:** Does the repo have one canonical Grid example (usually the app shell) that a new engineer can point to as "this is how we do it here"?

## How this topic typically evolves in a codebase

Teams start with Grid on exactly one element — the app shell. Everything else stays Flex because the team knows Flex. This is the correct starting posture and it survives well for months.

The first migration point comes when a card grid appears. Someone writes `display: flex; flex-wrap: wrap` with margin hacks, hits the "last row is left-aligned but should be evenly spaced" problem, and swaps in `repeat(auto-fill, minmax(...))`. From here Grid spreads through list-view pages.

The painful evolution is subgrid adoption. Before subgrid, teams built a design system with fixed section heights or JavaScript-measured alignment, and the CSS grew crufty. Migrating to subgrid means deleting a lot of that code, which is easy — but it also means auditing every card component to confirm the parent grid has the tracks the subgrid will inherit. In large codebases, that audit is the migration cost.

The final stage is a layout token system: named tracks (`--grid-content`, `--grid-feature`) and named areas (`--area-header`) get promoted to design tokens and shared across products. At that point, Grid stops being a per-page decision and becomes infrastructure.

## Further reading

- [MDN CSS Grid Layout guides](https://developer.mozilla.org/en-US/docs/Web/CSS/CSS_grid_layout) — the reference. Read the "Auto-placement" and "Subgrid" pages, not just the intro.
- [CSS Grid Level 2 spec, § 12 Track sizing algorithm](https://www.w3.org/TR/css-grid-2/#algo-track-sizing) — the only source of truth for why your `fr` track ended up the size it did.
- [Rachel Andrew, "Grid, content re-ordering and accessibility"](https://rachelandrew.co.uk/archives/2019/06/04/grid-content-re-ordering-and-accessibility/) — the definitive short read on why `order` and `dense` are accessibility hazards.
- [CSS-Tricks, "Auto-Sizing Columns in CSS Grid: `auto-fill` vs `auto-fit`"](https://css-tricks.com/auto-sizing-columns-css-grid-auto-fill-vs-auto-fit/) — the canonical visual comparison. Bookmark it.
- [Ahmad Shadeed, "Digging Into CSS Subgrid"](https://ishadeed.com/article/css-subgrid/) — practical patterns for card alignment, form layouts, and gallery grids.
- [Firefox DevTools Grid Inspector docs](https://firefox-source-docs.mozilla.org/devtools-user/page_inspector/how_to/examine_grid_layouts/) — Firefox's overlay still shows more (line names, area labels, track sizes in one view) than Chrome's; worth keeping Firefox installed just for Grid work.
