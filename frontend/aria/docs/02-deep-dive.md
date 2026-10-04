# ARIA (WAI-ARIA) — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

WAI-ARIA (Accessible Rich Internet Applications) is a W3C specification that defines an **ontology of roles, states, and properties** that authors attach to host-language elements (HTML, SVG) to change how those elements are exposed in the browser's **accessibility tree**, and from there to operating-system **accessibility APIs** consumed by assistive technology. The current Recommendation is [WAI-ARIA 1.2](https://www.w3.org/TR/wai-aria-1.2/) (published 6 June 2023, [W3C announcement](https://www.w3.org/WAI/news/2023-06-06/aria-12-rec/)). [WAI-ARIA 1.3](https://www.w3.org/TR/wai-aria-1.3/) is still a Working Draft; the latest draft is dated 4 June 2026.

ARIA is **declarative and semantic only**. It changes what the accessibility tree reports. It does not change focusability, keyboard handling, layout, or event dispatch. Everything else in this document follows from that.

### The core building blocks

- **Roles** (`role="tab"`): declare what an element *is*. ARIA 1.2 groups them into abstract roles (ontology-only, never authored, e.g. `widget`, `landmark`), widget roles (`button`, `tab`, `combobox`, `slider`), document structure roles (`heading`, `list`, `generic`), landmark roles (`navigation`, `main`, `search`), live region roles (`alert`, `status`, `log`), and window roles (`dialog`, `alertdialog`).
- **States** (`aria-expanded`, `aria-checked`, `aria-selected`, `aria-pressed`): values expected to change with user interaction. Your script owns keeping them true.
- **Properties** (`aria-label`, `aria-labelledby`, `aria-describedby`, `aria-controls`, `aria-required`): mostly static characteristics and relationships. IDREF properties create edges in the tree between elements that are not DOM ancestors of each other.
- **Live region attributes** (`aria-live`, `aria-atomic`, `aria-relevant`, `aria-busy`): tell the browser to raise announcement events when a subtree mutates.
- **Accessible name and description computation** ([accname 1.2](https://w3c.github.io/accname/)): the algorithm that turns markup into the single string a screen reader speaks for an element.
- **Accessibility API Mappings (AAMs)**: [Core-AAM](https://w3c.github.io/core-aam/) maps each ARIA role/state to platform APIs; HTML-AAM does the same for native HTML elements; the [ARIA in HTML](https://www.w3.org/TR/html-aria/) Recommendation defines which ARIA is *allowed* on which HTML element (what validators like axe check against).
- **[ARIA Authoring Practices Guide (APG)](https://www.w3.org/WAI/ARIA/apg/)**: non-normative patterns that specify the keyboard contract each widget role implies (tabs, combobox, treeview, grid, menu, dialog).

### How it relates to the broader landscape

ARIA belongs to the **semantic exposure layer** of accessibility, alongside native HTML semantics (which are always preferred) and the native mobile equivalents: Android `contentDescription`/`AccessibilityNodeInfo`, iOS `accessibilityTraits`, React Native `accessibilityRole`, Flutter `Semantics`. It is distinct from **WCAG**, which defines success criteria (ARIA helps you meet 4.1.2 Name, Role, Value), and from **component libraries** like React Aria or Radix, which ship the *behavior* ARIA does not. Two sibling ARIA modules extend the vocabulary for niche domains: DPUB-ARIA (digital publishing) and Graphics-ARIA (SVG charts).

## Where

### Where it runs / lives in the stack

Client side, in the browser engine, between the DOM and the operating system:

```
 HTML/CSS/DOM  ──►  Browser accessibility tree  ──►  Platform a11y API  ──►  Assistive tech
 (your aria-*)      (role, name, states,             (UIA / MSAA+IA2,        (JAWS, NVDA,
                     relations, events)               AX API, ATK/AT-SPI,     VoiceOver,
                                                      Android a11y)           TalkBack, Narrator)
        ▲                                                                          │
        └──────── your JS handles keydown/click, mutates DOM and aria-* ◄──────────┘
                                                        (user input)
```

The author writes attributes in the DOM. The browser maps them. The AT never sees your HTML directly; it sees platform objects such as `AXButton` on macOS or a UIA `Button` control type on Windows ([Core-AAM](https://w3c.github.io/core-aam/)).

### Where you typically encounter it

- **Headless/accessible component libraries**: React Aria (Adobe), Radix UI, Headless UI, Ariakit, Angular CDK `a11y`. ARIA is their primary output.
- **Design systems**: GOV.UK Frontend, Material, Fluent UI, Carbon. Every custom combobox, tabs, or menu in them is ARIA plus scripted keyboard handling.
- **SPA frameworks**: route-change announcements and toasts use live regions because there is no page load for the screen reader to notice.
- **Rich editors and grids**: Google Docs, AG Grid, and spreadsheet-style UIs use `role="grid"`, `aria-activedescendant`, and live regions heavily.
- **Test tooling**: Testing Library `getByRole` and Playwright `getByRole` query the computed role and name, so ARIA directly affects your selectors.

### Ecosystem and tooling

- **For authoring guidance**: APG patterns; [ARIA in HTML](https://www.w3.org/TR/html-aria/) for allowed attributes per element.
- **For automated checks**: axe-core (Deque; also powers Lighthouse's accessibility audit), WAVE (WebAIM), IBM Equal Access Checker, `eslint-plugin-jsx-a11y`.
- **For inspecting the tree**: Chrome DevTools Accessibility pane and full-page tree view, Firefox Accessibility Inspector, Accessibility Insights for Windows, macOS Accessibility Inspector.
- **For regression tests**: Playwright `toMatchAriaSnapshot` (since v1.49) asserts a YAML snapshot of the accessibility tree; Testing Library role queries.
- **For real AT testing**: NVDA (free, Windows), JAWS, VoiceOver (macOS/iOS), TalkBack (Android), Narrator. The W3C [ARIA-AT](https://aria-at.w3.org/) project publishes interoperability results for APG patterns across AT/browser pairs.

## When

### When the topic emerged and why

Around 2005, Ajax-era apps were building menus, trees, and tab panels out of `<div>` and `<span>`, and screen readers saw only text. IBM researchers Becky Gibson and Rich Schwerdtfeger described the fix in "DHTML Accessibility: Solving the JavaScript Accessibility Problem", and the W3C published the [WAI-ARIA Roadmap](https://www.w3.org/TR/wai-aria-roadmap) and first drafts in September 2006 ([W3C press release](https://lists.w3.org/Archives/Public/w3c-news/2006JulSep/0005.html)). Before ARIA, the options were: avoid scripted widgets, ship a separate "accessible version" of the app, or hack with hidden form controls. ARIA 1.0 became a Recommendation on 20 March 2014, 1.1 on 14 December 2017, and 1.2 on 6 June 2023 ([history](https://en.wikipedia.org/wiki/WAI-ARIA)). The long gap reflects the hard part: getting four browser engines and many screen readers to agree on mappings.

### When to use it in a project

Reach for it when:

- You need a widget HTML has no element for: tabs, treeview, combobox with filtering, grid, toolbar, feed.
- You must expose state that native HTML cannot express, e.g. `aria-expanded` on a disclosure trigger, `aria-current="page"` on a nav link, `aria-invalid` on a field after validation.
- You need a relationship across DOM positions: `aria-describedby` from an input to its error text, `aria-labelledby` from a dialog to its heading.
- Content updates without navigation and must be announced (status messages, route changes, async search results).
- An icon-only control has no visible text (`aria-label="Close"`).

### When NOT to use it

Avoid it when:

- A native element exists: `<button>`, `<a href>`, `<dialog>`, `<details>/<summary>`, `<select>`, `<input type="checkbox">`, `popover`. Native elements bring focus, keyboard, and platform mappings for free.
- You will not implement the full keyboard contract of the role. `role="menu"` promises arrow-key navigation and typeahead; a site nav labeled as a menu with only Tab support breaks expectations.
- You are adding it "just in case." Redundant ARIA (`<button role="button">`) is noise; wrong ARIA overrides correct native semantics.
- You are on a non-web surface (native iOS/Android, Flutter, terminal). Use that platform's accessibility API.

## How

### How it works under the hood

1. **Parse and style.** The engine builds the DOM and computed style. `display: none`, `visibility: hidden`, the `hidden` attribute, `inert`, and `aria-hidden="true"` all remove nodes from the accessibility tree (with different side effects on focus and rendering).
2. **Compute role.** Explicit `role` wins over the implicit HTML role if it is valid for that element; otherwise the HTML-AAM implicit mapping applies (`<nav>` becomes `navigation`, `<div>` becomes `generic`). Invalid or abstract roles are ignored and the engine falls back.
3. **Compute name and description** using accname 1.2, in strict precedence ([spec](https://w3c.github.io/accname/)):
   - 2A: hidden and not referenced → empty
   - 2B: `aria-labelledby` (follows IDREFs, can pull text from hidden nodes)
   - 2C: embedded control value (when computing a label that contains an input)
   - 2D: `aria-label`
   - 2E: host-language label (`<label>`, `alt`, `<caption>`, SVG `<title>`)
   - 2F–2H: name from content, recursing into children, only for roles that allow it (button, link, tab, heading, and others)
   - 2I: tooltip (`title`) as a last resort

   So `aria-labelledby` beats `aria-label`, which beats `<label>`, which beats inner text.
4. **Compute states and relations.** `aria-*` states are normalized (invalid tokens fall back to defaults), and IDREF relations are resolved within the same DOM tree scope. A plain `id` reference cannot cross a shadow root boundary.
5. **Map and serialize.** The engine maps each node to platform objects per Core-AAM: `button` becomes `ROLE_SYSTEM_PUSHBUTTON` (MSAA), UIA `Button`, ATK `ROLE_PUSH_BUTTON`, `AXButton` (macOS). Adding `aria-pressed` turns it into a toggle-button mapping ([Core-AAM](https://w3c.github.io/core-aam/)). In multi-process browsers the tree is serialized from the renderer to the browser process, which answers AT queries.
6. **AT consumption.** Windows screen readers (JAWS, NVDA) build a **virtual buffer** and default to **browse mode**, where they intercept arrow keys and letter shortcuts (H for heading, etc.). On focus of a widget like a textbox or `grid`, they switch to **focus mode** and pass keys through to the page ([tink.uk](https://tink.uk/understanding-screen-reader-interaction-modes/)). `role="application"` forces focus mode for a whole subtree.
7. **Your script runs.** Keydown handlers move focus (roving `tabindex`) or update `aria-activedescendant`, and flip states like `aria-selected`. Each mutation fires a platform event (focus, state change, name change) and the AT announces it.
8. **Live regions.** When a node inside a region with `aria-live="polite"` (or `role="status"`) changes, the browser fires a live-region-changed event. `polite` waits for the user to go idle; `assertive` interrupts. The region must already exist in the accessibility tree before its content changes, or the change is often missed.

A correct **tabs** widget per APG needs all of this: `role="tablist"` containing `role="tab"` elements with `aria-selected` and `aria-controls`, `role="tabpanel"` with `aria-labelledby` pointing back to its tab, one tab in the Tab sequence (`tabindex="0"`, others `-1`), Left/Right arrows to move between tabs, Home/End to jump, and a decision between automatic activation (arrow selects) and manual activation (arrow focuses, Enter/Space selects). See `code/` for a runnable version.

### Key trade-offs

| Choice | You gain | You give up |
|---|---|---|
| Native element vs. ARIA on a `<div>` | Native: free focus, keyboard, form participation, consistent mappings | Native: styling limits (only partly fixed by `appearance: base-select` in Chromium) |
| `aria-labelledby` vs. `aria-label` | Labelledby reuses visible text, so name and screen match and auto-translate covers it | Needs stable, unique IDs in the same tree scope |
| Roving `tabindex` vs. `aria-activedescendant` | Roving: real DOM focus, predictable across AT | Roving: more focus juggling. Activedescendant keeps focus on a container (useful for combobox inputs) but AT support is less uniform |
| `aria-live` region vs. `ariaNotify()` | Live regions work everywhere today | `ariaNotify()` (Chrome 141, Firefox 150, not Safari per [web.dev](https://web.dev/blog/web-platform-04-2026)) avoids DOM hacks but lacks full cross-browser support |
| `polite` vs. `assertive` | Assertive: immediate | Assertive interrupts whatever the user was hearing. Reserve it for errors |
| `aria-hidden` vs. `inert` vs. `display:none` | `aria-hidden`: hides from AT only, stays visible | Focusable children remain reachable by keyboard. `inert` removes both AT exposure and focus |
| Automatic vs. manual tab activation | Automatic: fewer keystrokes | Manual is required when loading a panel is slow or triggers network calls |

### Common failure modes

- **"Button" that does nothing on Enter.** Cause: `role="button"` on a `<div>` without `tabindex="0"` and keydown handling for Enter and Space.
- **Focus disappears into a void.** Cause: `aria-hidden="true"` on an ancestor of a focused or focusable element. Chrome now blocks it on a focused subtree and logs "Blocked aria-hidden on an element because its descendant retained focus", recommending `inert`.
- **Toast never announced.** Cause: live region inserted into the DOM in the same tick as its text, or toggled from `display:none`.
- **Wrong name spoken.** Cause: `aria-label` overriding visible text (also breaks voice control: the user says "click Submit" but the name is "btn-primary").
- **`aria-labelledby` silently empty.** Cause: duplicate IDs, a typo, or the referenced node lives inside a different shadow root.
- **Site nav unusable.** Cause: `role="menu"`/`menuitem` on links, which puts AT into a menu interaction model the page does not implement.
- **State lies.** Cause: `aria-expanded` or `aria-selected` set at render and never updated by the click handler; framework re-renders reset attributes.
- **Mobile users stuck.** Cause: keyboard-only patterns (arrow keys) with no touch or swipe equivalent; VoiceOver on iOS does not send arrow keys.

## Why

### Why it exists

Accessibility is a **separation-of-concerns problem**: visual presentation and semantic meaning travel on different channels. A sighted user infers "this is a tab, it's selected" from pixels. A screen reader user receives only what the platform accessibility API reports. Once the web started rendering its own controls instead of using OS widgets, the semantic channel went silent. ARIA restores it with the smallest possible mechanism: metadata on existing elements, mapped by the browser into the APIs ATs already understand. This is also a legal concern now: WCAG 2.1/2.2 success criterion 4.1.2 (Name, Role, Value) is effectively unmeetable for custom widgets without ARIA.

### Why it looks the way it does

The obvious alternative was **new HTML elements for every widget** (`<tabs>`, `<tree>`, `<combobox>`). That would bundle semantics and behavior correctly, but each element needs years of standardization and implementation in every engine, and authors in 2006 needed something that worked on existing markup immediately. ARIA chose **attributes, not elements**, and **semantics, not behavior**:

- Attributes are backwards compatible. Browsers that do not understand them ignore them; layout is unaffected.
- Behavior was left to authors because widget behavior varies by platform and design, and specifying it would have reintroduced the "new elements" problem.

The cost of that choice is the central failure mode of ARIA: semantics and behavior can drift apart, and nothing in the browser enforces the contract. That is why the [Using ARIA](https://www.w3.org/TR/2018/WD-using-aria-20180115/) note's rules read like guardrails: (1) use native HTML if you can, (2) do not change native semantics unless you must, (3) all interactive ARIA controls must be keyboard operable, (4) never put `role="presentation"` or `aria-hidden="true"` on a focusable element, (5) all interactive elements must have an accessible name.

The original alternative is now coming back in stages: `<dialog>`, `popover`, `inert`, and customizable `<select>` let HTML carry both semantics and behavior, shrinking ARIA's job to the remaining gaps.

### Why it matters now

Three pressures make ARIA more relevant in 2026, not less.

- **Regulation.** The European Accessibility Act has applied since 28 June 2025. In the US, the DOJ's ADA Title II rule requires WCAG 2.1 AA for state and local governments; an April 2026 interim final rule pushed deadlines to April 2027 and April 2028 ([JD Supra](https://www.jdsupra.com/legalnews/department-of-justice-delays-compliance-2688927/)).
- **Volume and quality.** The [WebAIM Million 2026](https://webaim.org/projects/million/) found ARIA on 82.7% of top home pages, averaging 133.6 attributes per page (up 27% in a year, over six times 2019 levels). Pages with ARIA averaged 59.1 detected errors versus 42.0 without. WebAIM attributes rising errors partly to third-party frameworks and AI-assisted coding, which tend to sprinkle `aria-*` without the matching behavior.
- **Platform evolution.** Element reflection (`ariaLabelledByElements`, Baseline since April 2025 per [MDN](https://developer.mozilla.org/docs/Web/API/Element/ariaLabelledByElements)), `ariaNotify()`, and Reference Target for cross-shadow-root ARIA (approved for Chrome 151 in mid-2026, [blink-dev](https://groups.google.com/a/chromium.org/g/blink-dev/c/eBx3sXIfJnw)) address long-standing gaps for Web Components. ARIA 1.3 drafts add `aria-description`, `aria-braillelabel`, and the `suggestion`/`comment`/`mark` roles.

The AT market you test against is also concrete. [WebAIM Screen Reader Survey #11](https://webaim.org/projects/screenreadersurvey11/) (July–August 2026, 1,780 responses) reports primary desktop screen readers as JAWS 55.0%, NVDA 32.9%, VoiceOver 6.5%, and 91.8% of respondents also use a screen reader on mobile.

## Open questions / things to verify in practice

- Does your tabs widget announce "tab, 2 of 4, selected" identically in NVDA + Firefox, JAWS + Chrome, and VoiceOver + Safari? Where does each pairing diverge?
- What does the Chrome DevTools accessibility tree show as the computed name when `aria-labelledby`, `aria-label`, and visible text all conflict? Does it match accname step order?
- How long must a live region exist before an update is reliably announced, and does a framework re-render that replaces the region node break it?
- Is `aria-activedescendant` on a combobox input announced consistently, or does roving focus behave better with your target AT?
- With `ariaNotify()` available in Chrome and Firefox, what fallback do you ship for Safari, and does double-announcing occur?
- Does your widget remain operable with VoiceOver on iOS and TalkBack swipe gestures, where arrow-key handlers never fire?
