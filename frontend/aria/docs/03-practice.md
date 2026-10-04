# ARIA (WAI-ARIA) — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In most product codebases, ARIA lives in the **design system**. The `Combobox`, `Tabs`, `Menu`, `Dialog`, and `Toast` components are where `role`, `aria-*`, and keyboard handlers live. Feature code mostly touches ARIA indirectly, by passing a `label` prop or picking the wrong component.

In a single-page app, ARIA does the work the browser used to do on a page load: announcing route changes, async search results, "saved" toasts, and validation errors through live regions. In data-heavy tools (file trees, spreadsheets, admin grids), it carries the heaviest load: `role="tree"`/`grid`, `aria-activedescendant`, `aria-level`, and hundreds of nodes whose state must stay accurate.

You also meet it in audits and legal tickets. The European Accessibility Act has applied since June 2025, and WCAG 4.1.2 (Name, Role, Value) is the criterion custom widgets fail. When the ticket says "screen reader announces 'clickable' with no name," the fix is ARIA, or better, removing a `<div>` in favor of a native element.

## Best practices

### 1. Default to native elements and make custom roles go through review
**Do:** Use `<button>`, `<a href>`, `<dialog>`, `<details>`, `<select>`, `popover`, and `<input type="checkbox">` first. Treat any new `role=` on an interactive element as a design-review item, not a code-review nit.
**Why:** Native elements bring focus, Enter/Space, form submission, and platform mappings for free. Every `<div role="button">` is a future bug where Enter does nothing (see `02-deep-dive.md § Common failure modes`).
**Avoid:** Styling a `<div>` because "the button had default margins."

### 2. Keep ARIA in a few components, not across hundreds of call sites
**Do:** Put every composite widget in one place: your design system, or a headless library (React Aria, Radix, Ariakit, Angular CDK). Feature code passes `label`, `description`, and `items`, never raw `aria-*`.
**Why:** A bug in one shared `Combobox` is one fix. Forty hand-rolled dropdowns mean forty slightly different keymaps and forty audit findings.
**Avoid:** Each feature team writing its own `onKeyDown` for arrow keys.

### 3. Ship the full APG keyboard contract, or don't claim the role
**Do:** For every widget role, write the keymap from the APG pattern into the component's docs (Tabs: Left/Right, Home/End, activation mode) and add one test per key.
**Why:** The role tells the screen reader to switch to focus mode and tells the user which keys to press. If your `role="menu"` ignores arrow keys, the user is stuck in a menu that doesn't respond.
**Avoid:** Adding `role="menu"` to site navigation because the design looks like a menu.

### 4. Derive ARIA state from the same source as visual state
**Do:** Bind `aria-expanded={open}` to the same variable that drives the CSS. Better still, style from the attribute (`[aria-expanded="true"] .chevron { rotate: 180deg }`) so the visuals cannot drift from the semantics.
**Why:** The usual "state lies" bug comes from an imperative `setAttribute` in one handler that a later re-render or a second code path never updates. Sighted QA never notices.
**Avoid:** An `isOpen` CSS class and a separate `aria-expanded` that only the click handler updates.

### 5. Name controls from visible text; reserve `aria-label` for icon-only controls
**Do:** Use `<label for>`, inner text, or `aria-labelledby` pointing at visible text. Use `aria-label` only where nothing visible exists (`aria-label="Close"` on an X icon), and write it in user language.
**Why:** WCAG 2.5.3 (Label in Name) requires the accessible name to contain the visible label. A voice-control user who says "click Submit order" fails when the name is "btn-primary." Translation tools also handle visible text more reliably than attribute values.
**Avoid:** `aria-label` on a button that already has visible text, which overrides that text.

### 6. Mount live regions once at app start and change only their text
**Do:** Render one `role="status"` (polite) and one `role="alert"` (assertive) region in the app shell on first paint. Expose an `announce(message, priority)` helper. Clear the region, then set the text on the next frame. Keep messages under one sentence.
**Why:** A region inserted into the DOM along with its text is often never announced. Framework re-renders that replace the region's node have the same effect. Centralizing the regions also stops two toasts from talking over each other.
**Avoid:** A `<Toast>` component that renders its own `<div aria-live>` alongside the message.

### 7. Use `<dialog>.showModal()` or `inert` for modality, not `aria-hidden` sweeps
**Do:** Open modals with `showModal()`, which makes the rest of the document inert. For custom overlays, set `inert` on the background. On close, return focus to the trigger.
**Why:** `aria-hidden="true"` on a container hides it from screen readers but leaves its buttons focusable, so keyboard users tab into content they can't hear. Chrome now blocks `aria-hidden` on a focused subtree and logs a warning recommending `inert`.
**Avoid:** Setting `aria-modal="true"` and trusting it to trap focus. It doesn't.

### 8. Default to roving `tabindex`; use `aria-activedescendant` for combobox inputs
**Do:** In trees, toolbars, tablists, and listboxes, move real DOM focus (`tabindex="0"` on the active item, `-1` on the rest). Use `aria-activedescendant` mainly where focus must stay in a text input, as in a combobox.
**Why:** Real focus behaves predictably across AT. GitHub's file-tree team chose roving `tabindex` because `aria-activedescendant` caused problems with VoiceOver on macOS and iOS.
**Avoid:** Using `aria-activedescendant` everywhere because it means less focus management code.

### 9. Generate stable, unique IDs and keep references in the same tree scope
**Do:** Generate IDs per instance (`useId()` in React, a counter elsewhere). In Web Components, keep the label inside the same shadow root, or use element reflection (`el.ariaLabelledByElements = [...]`).
**Why:** A duplicate or out-of-scope ID breaks `aria-labelledby` silently. The name is empty, no error is thrown, and axe flags it only on pages where the duplicate actually renders.
**Avoid:** Hard-coding `id="field-label"` inside a component that appears twice on a page.

### 10. Query by role in tests so broken semantics fail the build
**Do:** Write `getByRole('tab', { name: 'Billing', selected: true })` in Testing Library or Playwright. Add `toMatchAriaSnapshot` for complex widgets.
**Why:** A test that uses role queries fails when someone swaps `<button>` for `<div>`. A test that uses `data-testid` keeps passing on markup a screen reader can't use.
**Avoid:** `data-testid` as the default selector strategy.

### 11. Layer your testing: automated in CI, manual AT before release
**Do:** Run `eslint-plugin-jsx-a11y` and axe-core on every PR. Before shipping a new widget, test it by hand on NVDA + Chrome or Firefox, JAWS + Chrome, VoiceOver + Safari on macOS and iOS, and TalkBack + Chrome.
**Why:** Experts disagree on how much automation catches. Deque's 2021 study of about 300,000 issues found that axe caught 57% *by volume*. The older rule of thumb, counted by WCAG criteria, says 20–30%. Either way, axe cannot press Enter or check whether `aria-selected` is accurate after a click, so behavior bugs always need a human.
**Avoid:** Treating a Lighthouse score of 100 as sign-off.

## Anti-patterns to recognize

- **Belt-and-braces ARIA**: `<nav role="navigation">`, `<button role="button" tabindex="0">`, `aria-label` on a plain `<div>`. It looks careful, but it's noise at best. `generic` elements prohibit naming in ARIA 1.2, so that label is ignored or inconsistently exposed. Delete it, and add ARIA only where native HTML leaves a gap.
- **Site nav as `role="menu"`**: dropdown navigation marked up with `menubar`/`menuitem` because it looks like a desktop menu. Screen readers announce application-menu semantics and expect arrows, Esc, Home/End, and typeahead, while the links lose their link role. Use a list of links with disclosure buttons (`aria-expanded`) for the submenus ([Adrian Roselli](https://adrianroselli.com/2017/10/dont-use-aria-menu-roles-for-site-nav.html)).
- **`role="application"` to make keyboard handling work**: added to a whole panel so screen readers stop capturing arrow keys. It also removes browse mode, so users can no longer read the content inside with their usual shortcuts. Use the specific widget role (`grid`, `listbox`, `tree`) scoped to the interactive part.
- **Everything is `assertive`**: every toast uses `role="alert"`. Users get interrupted mid-sentence for "Draft saved," and they learn to ignore alerts. Use `status`/polite by default and keep assertive for errors that block progress.
- **Accessibility overlays**: a third-party script that injects ARIA and "fixes" at runtime. It patches the DOM after render, conflicts with your own ARIA, and doesn't make the site compliant. The FTC fined accessiBe $1M (order finalized April 2025) for claiming it did. Fix the source.
- **Generated ARIA nobody tested**: code from AI assistants or copied snippets that adds `aria-*` without the matching behavior. WebAIM's 2026 scan found pages with ARIA averaging 59.1 detected errors against 42.0 without, and partly blamed frameworks and AI-assisted code. Run lint rules and require a keyboard walkthrough in review for any new `role=`.
- **Copying APG examples as production code**: the APG examples are reference demos. Their support varies across AT/browser pairs, as [ARIA-AT](https://aria-at.w3.org/) results show. Use them for the keyboard contract, then test in your own AT matrix.

## Real-world usage patterns

**Code-hosting file tree.** GitHub's repository file browser is a `role="tree"` with thousands of nodes ([GitHub blog](https://github.blog/engineering/user-experience/considerations-for-making-a-tree-view-component-accessible/)). The team modeled keyboard behavior on Windows File Explorer, used roving `tabindex`, and set `aria-level` explicitly on every item. They also named nodes with `aria-labelledby`, because VoiceOver didn't reliably derive names from complex content, and they chose not to virtualize the tree. *Lesson:* in production you often have to set attributes the spec says the browser can compute, because real AT doesn't compute them reliably.

**Government autocomplete.** GOV.UK's accessible-autocomplete is a combobox that uses a live region to announce the result count and the minimum number of characters to type. In comparative testing, NVDA users preferred it to the ARIA 1.1 reference pattern, which said nothing after the user typed a few letters. *Lesson:* correct roles are the minimum. Announcement wording is UX work and decides whether users finish the task.

**SPA route changes.** Client-side navigation fires no page load, so screen readers say nothing. Next.js ships a built-in route announcer that reads `document.title`, then the first `<h1>`, then the pathname ([Next.js docs](https://nextjs.org/docs/architecture/accessibility)). *Lesson:* page titles now feed what screen readers announce. A generic title like "App" on every route means every navigation announces "App." Teams disagree on whether to announce the new page or move focus to its `<h1>`. Moving focus gives a more reliable announcement, but it can disorient sighted keyboard users if the focus ring isn't styled well.

**Checkout form validation.** Each field gets `aria-invalid="true"` and `aria-describedby` pointing at its error text. On submit, focus moves to an error summary at the top with links to each field. *Lesson:* don't put per-keystroke validation in a live region. It announces "invalid email" on every character typed. Validate on blur or submit, and let focus movement deliver the message.

## Operational checklist

- Does every interactive custom element have a role, an accessible name, and keyboard support? Check the Chrome DevTools Accessibility pane, not the source.
- Is every ARIA state (`aria-expanded`, `aria-selected`, `aria-checked`) bound to the same state variable that drives the visuals, with a test that toggles it?
- Is there exactly one polite and one assertive live region, mounted at app start, behind an `announce()` helper?
- Do modals use `showModal()` or `inert`, return focus to the trigger on close, and contain no `aria-hidden` on an ancestor of focusable content?
- Do axe-core and `jsx-a11y` (or the equivalent for your framework) run in CI and fail the build on serious and critical violations?
- Do component tests select by role and name rather than `data-testid`?
- Is there a written AT test matrix (NVDA, JAWS, VoiceOver macOS and iOS, TalkBack), and was every new widget run through it before release?
- Are IDs generated per instance, and do any ARIA references cross a shadow-DOM boundary?
- Does every route set a unique, descriptive `document.title`?
- Does a new engineer know the rule on day one: native element first, and a new `role=` only through the design system?

## How this topic typically evolves in a codebase

Most projects start with native HTML plus a few `aria-label`s on icon buttons, and that works. The trouble starts when designers ask for custom dropdowns, tabs, and date pickers, and each feature team builds its own from `<div>`s. ARIA spreads to hundreds of call sites, each with a slightly different keymap, and nobody notices because sighted mouse testing passes.

The turning point is usually external: an enterprise customer's accessibility audit, a VPAT request, a demand letter, or an EAA deadline. The audit returns a list of findings across dozens of near-identical widgets, and the team learns that fixing them one by one doesn't scale. The expensive migration is consolidating into one component library (in-house or React Aria/Radix): finding every hand-rolled widget, rewriting `data-testid` tests into role queries, and re-testing with AT. Teams that hand-rolled combobox markup also paid for spec changes. The combobox pattern changed from ARIA 1.0 to 1.1 and then partly reverted in 1.2, so the 1.1 implementations had to be reworked.

Mature codebases end up with *less* ARIA, not more. As `<dialog>`, `popover`, `inert`, and customizable `<select>` become available in every major browser, the best ARIA work is deleting it: removing focus traps, `aria-modal` workarounds, and custom listboxes in favor of elements that handle semantics and behavior together. Plan for that by keeping ARIA inside a few components, so removing it later touches only those files.

## Further reading

- [ARIA Authoring Practices Guide](https://www.w3.org/WAI/ARIA/apg/): the keyboard contract for every widget pattern. Read "Read Me First" ("No ARIA is better than bad ARIA") before any pattern.
- [ARIA in HTML](https://www.w3.org/TR/html-aria/): the allowed-ARIA-per-element table that axe and validators enforce. Use it to settle "is this role allowed here?" debates.
- [Considerations for making a tree view component accessible](https://github.blog/engineering/user-experience/considerations-for-making-a-tree-view-component-accessible/) (GitHub): a detailed account of the interoperability compromises in a large-scale ARIA widget.
- [Don't Use ARIA Menu Roles for Site Nav](https://adrianroselli.com/2017/10/dont-use-aria-menu-roles-for-site-nav.html) (Adrian Roselli): the standard explanation of the most common ARIA misuse.
- [ARIA-AT](https://aria-at.w3.org/): measured support for APG patterns per screen reader and browser pair. Check it before trusting a pattern.
- [WebAIM Million](https://webaim.org/projects/million/): yearly data on how ARIA is actually used and misused across the top million home pages.
