# Web Components — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

The single most common production home for Web Components is a **cross-framework design system**. A platform team owns a `<acme-button>`, `<acme-modal>`, `<acme-datepicker>` set, ships them once, and the React app, the Angular admin, the Vue marketing site, and the legacy Rails partial all consume the same tag. Adobe Spectrum, Microsoft Fluent, SAP UI5, GitHub Primer, Shoelace, and Ionic all follow this shape; if your company has more than one frontend framework in play and cannot re-standardize, this is why you are looking at custom elements.

The second common home is an **embeddable widget**. Chat bubbles (Intercom-style), checkout drop-ins (Stripe Elements-style), consent banners, live-support widgets, and price-comparison overlays live inside unknown host pages with unknown global CSS. Shadow DOM is the only mainstream browser primitive that keeps their styling and DOM structure from being clobbered by the host, short of an iframe — and iframes cost a document, a network context, and worse a11y.

The third home is **micro-frontends** — Salesforce LWC being the enterprise poster child. Each team ships a bundle exposing custom-element tags; the shell composes tags. Interop is by tag name and CustomEvent, not by shared React contexts.

You will rarely reach for a Web Component to build a whole SPA. The technology's sweet spot is *leaf-level, high-reuse UI primitives* — not routing, not global state, not data fetching.

## Best practices

### 1. Do the DOM work in `connectedCallback`, not the constructor
**Do:** Reserve the constructor for `super()`, class field init, and `attachShadow`. Stamp the template, wire listeners, and read attributes in `connectedCallback`.
**Why:** The spec forbids child mutations in the constructor and browsers throw `NotSupportedError`. It also means SSR / Declarative Shadow DOM parses cleanly, and elements can be `document.createElement`'d without side effects.
**Avoid:** Calling `innerHTML =`, `appendChild`, or `fetch` in the constructor because "it works in Chrome."

### 2. Reflect properties to attributes for anything that belongs in HTML
**Do:** For boolean/string state that a user might want to set in markup (`disabled`, `open`, `variant="primary"`), pair a getter/setter with an `attributeChangedCallback` and mirror the value both ways. Follow the built-in HTML pattern: attribute presence = boolean true.
**Why:** Server-rendered pages, view-source debugging, framework attribute-binding (React 18, Angular templates) and CSS selectors like `[disabled]` all rely on the attribute being real. Without reflection you get silent state drift between DOM and JS.
**Avoid:** Storing state only on the class instance and expecting external observers or CSS to see it.

### 3. Declare `observedAttributes` explicitly and keep the list small
**Do:** List only the attributes that are part of your public API. Handle unknown attribute changes with a MutationObserver only if you truly need dynamic keys.
**Why:** The browser skips `attributeChangedCallback` for anything not observed as a performance optimization. A missing entry is the #1 reason "my attribute update does nothing." A bloated list turns every `class="…"` toggle into a callback.
**Avoid:** Blindly listing every attribute or forgetting to add a new one when the API grows.

### 4. Dispatch typed, composed CustomEvents — never call parent methods
**Do:** `this.dispatchEvent(new CustomEvent('acme-change', { detail: { value }, bubbles: true, composed: true }))`. Prefix event names, use `detail` for payload, and document them.
**Why:** `composed: true` is what lets the event cross the shadow boundary; without it your host page sees nothing. Prefixing prevents collisions with future native events. Method calls up the tree tie a component to a specific parent and break reuse.
**Avoid:** Un-prefixed generic names like `change` (collides with native form events) or forgetting `composed`.

### 5. Clean up everything in `disconnectedCallback`
**Do:** Remove event listeners added to `window`/`document`, disconnect `ResizeObserver`/`MutationObserver`/`IntersectionObserver`, cancel `AbortController`s, and clear timers. Use an `AbortSignal` passed to every `addEventListener` for one-line teardown.
**Why:** Custom elements are re-inserted during framework re-renders and route changes; leaked listeners survive across page navigations in SPA hosts, causing a slow-growing memory leak and duplicated handlers.
**Avoid:** Assuming GC will clean it because "the element is gone" — the listener on `window` still holds a hard reference.

### 6. Share stylesheets with `adoptedStyleSheets` / Constructable Stylesheets
**Do:** Build one `CSSStyleSheet` per component class, reuse it across every instance via `shadowRoot.adoptedStyleSheets = [sheet]`. Lit does this for you.
**Why:** Cloning a `<style>` tag into 500 shadow roots costs 500 parses and 500 style engines. Adopted sheets share a single parsed rule set; measurable render-time win on component-heavy pages.
**Avoid:** `shadowRoot.innerHTML = '<style>…</style>'` for large stylesheets in high-count components.

### 7. Theme through CSS custom properties and `::part`, never through selectors that pierce
**Do:** Expose design tokens as `--acme-color-primary`; expose stylable internals via `part="trigger"`. Document both like a public API.
**Why:** Custom properties inherit through shadow boundaries by spec; `::part` gives consumers explicit hooks. Anything else (deep selectors, `!important`, unsafe workarounds) breaks the moment you refactor internals — and it will.
**Avoid:** Encouraging consumers to reach into `shadowRoot` with `>>>` shims. That is a public-API breach waiting for a bug report.

### 8. Use `ElementInternals` for form-associated components
**Do:** Add `static formAssociated = true` and `this.#internals = this.attachInternals()`. Use `setFormValue()`, `setValidity()`, and the ARIA reflection setters (`ariaLabel`, `role`).
**Why:** Without form association your `<acme-input>` is invisible to `<form>` submission, `FormData`, native validation, and reset. ARIA reflection avoids the classic "custom element with no accessible name" a11y bug (WebKit shipped this in 16.4; Firefox in 126).
**Avoid:** Faking form participation with a hidden `<input>` proxy — it duplicates state and breaks with server-rendered forms.

### 9. Guard first paint against FOUC and upgrade flashes
**Do:** Ship `:not(:defined) { visibility: hidden }` in a global stylesheet, or use Declarative Shadow DOM so the first paint is already encapsulated. `customElements.whenDefined('acme-button')` gates any JS that assumes the element is upgraded.
**Why:** Undefined elements render as inline boxes of unstyled content; users see a jarring flash on cold loads, and layout thrash on hydration.
**Avoid:** Blocking rendering behind a top-of-body `<script>` — that costs TTFB more than the FOUC ever cost you.

### 10. Ship as ESM, side-effect free per import, with a single `define()` call gated
**Do:** Export the class and provide a separate `define.js` (or an idempotent `if (!customElements.get(...)) customElements.define(...)` block). Publish as `"type": "module"`, mark `"sideEffects": false` where possible, and version the tag name (`acme-button-v2`) if the API is breaking.
**Why:** Two copies of your library on one page — a design-system upgrade half-rolled-out, a micro-frontend collision — will throw on the second `define()` and crash the host. Global tag names are a single namespace; treat them like it. Tree-shakable ESM lets consumers pull one component without the whole kit.
**Avoid:** UMD bundles that eagerly `define()` on import, or unconditional `define()` calls with no guard.

### 11. Test with a runner that understands Shadow DOM
**Do:** Use `@web/test-runner` + `@open-wc/testing` for unit tests, Playwright for E2E. Playwright's locators pierce open shadow roots automatically; use `getByRole` / `getByText`. For closed roots, expose test hooks intentionally.
**Why:** Jest + jsdom historically has broken/partial custom element support; you will chase phantom failures. Real-browser test runners exercise the actual upgrade + reaction queue.
**Avoid:** `querySelector` chains through `.shadowRoot.shadowRoot.shadowRoot` in tests — they encode implementation details and break on refactor.

### 12. Match the framework interop story to your consumer list
**Do:** For React <19, ship a wrapper generated by `@lit/react` that maps props to properties and event listeners to `on*` handlers. For Vue, document the `.prop` modifier and set `compilerOptions.isCustomElement`. For Angular, add `CUSTOM_ELEMENTS_SCHEMA` and rely on `[prop]` / `(event)` bindings.
**Why:** React 18 stringifies unknown props into attributes, silently mangling booleans and objects. React 19 fixed most of it but object-typed properties without an existing setter still coerce. Angular's schema is required or the compiler throws.
**Avoid:** Assuming "it's just a tag" — every framework has one edge case, and your users will find it before you do.

## Anti-patterns to recognize

- **Shadow DOM as security boundary**: Devs assume closed roots protect secrets. Closed only hides from same-page JS with a reference; anyone can `Object.defineProperty` on `attachShadow` before your script runs, and CSS/DOM in the shadow is still in the same JS realm — use iframes with `sandbox` for real isolation.
- **Constructor-heavy elements**: Doing template stamping, network calls, or observer setup in the constructor. Every `document.createElement('x-foo')` (including framework diffing that speculatively creates then discards) pays that cost. Move it to `connectedCallback`.
- **Slot-blind global CSS**: Forgetting that slotted content stays in light DOM. Authors write `slot > *` styles inside the shadow, users see host-page styles winning, and everyone blames Shadow DOM. Design tokens + `::slotted(*)` (simple selector) is the sanctioned path.
- **Attribute-only APIs for complex data**: Serializing a JSON object to an attribute so React 18 can pass it. Every re-render re-parses. Use properties, wrap for React, and reserve attributes for HTML-native primitives.
- **Nested shadow root fractals**: Ten-deep shadow trees to "componentize everything." Event retargeting becomes untraceable, `::part` doesn't forward past one level without explicit `exportparts`, and DevTools inspection is miserable. Flat is better.
- **Global `define()` in a shared library entry**: A design system whose `import '@acme/ds'` calls `define()` for 40 tags on first load. Second copy on the page (older micro-frontend) throws; consumers pay for all 40 whether they use one or not. Per-component entry points with guarded define.
- **Framework re-render blast radius**: React remounting a `<my-chart>` on every parent render because a prop identity changed. Each cycle runs disconnect + reconnect + reinit; charts flash. Memoize props at the boundary or wrap with `@lit/react`.
- **Custom element as SPA root**: Turning `<my-app>` into a mini-framework with routing and stores inside a shadow root. You lose SSR, DevTools ergonomics, and you have re-invented React badly. Use a framework for app shell, custom elements for reusable widgets.

## Real-world usage patterns

- **Enterprise design system across polyglot teams.** A financial services company runs React (customer web), Angular (internal admin), and legacy JSP portals. The platform team ships `@bank/ui` as ~60 Lit-based custom elements plus per-framework wrappers. *Lesson:* the wrappers are as much product as the elements — dropping `@lit/react` in favor of "React handles it now" broke tabular data props for pre-React-19 consumers for six months.

- **Embeddable checkout / consent widget.** A payments provider replaces its iframe-based drop-in with a Shadow DOM widget for better a11y and first-paint. Host CSS `* { box-sizing: content-box }` used to nuke the iframe fallback; shadow encapsulation cures it. *Lesson:* they still expose `--pay-radius`, `--pay-font` custom properties because customers demanded visual matching — the theming API becomes the actual product surface.

- **CMS island architecture.** A marketing site is 95% static HTML from a headless CMS with a handful of interactive islands: `<price-calculator>`, `<video-hero>`, `<map-locator>`. Each ships as a single ESM file loaded with `<script type="module" async>`. *Lesson:* the team standardized on Declarative Shadow DOM for the hero so LCP measures the actual painted content, not a post-hydration flash — before, Lighthouse penalized them 8 points.

- **Salesforce LWC-style platform.** An internal app platform where line-of-business teams ship "apps" as bundles of custom elements composed by a shell. Version skew is the daily reality. *Lesson:* they version tag names (`acme-grid-v3`) instead of assuming Semver in tag registration, because two versions of the same tag cannot coexist under a single global registry (scoped registries are still not shipped in Safari as of 2026).

- **GitHub-style progressive enhancement.** Server-rendered pages sprinkle in `<clipboard-copy>`, `<relative-time>`, `<details-menu>` — each tiny, dependency-free, and layered on top of already-usable HTML. *Lesson:* the "works before JS loads, better after" contract is what keeps the site usable on slow connections and inside search-engine renderers that timeout on heavy hydration.

## Operational checklist

- **Monitoring:** Are you counting time-to-`whenDefined` for critical tags on real user monitoring? A regression here shows up as a subtle LCP degradation before anyone notices FOUC.
- **Failure handling:** If `customElements.define()` throws (duplicate registration, invalid name), does the app degrade or crash? Have you tested loading two library versions on the same page?
- **Framework interop:** Is there a snapshot test per supported framework version (React 18, React 19, Vue 3, Angular 17+) exercising boolean, object, and event bindings? These regress silently on framework upgrades.
- **Accessibility:** For every form-associated element, is `ElementInternals` wired for `role`, `ariaLabel`, and validity? Has an actual screen reader (VoiceOver, NVDA) been through the shadow tree?
- **Security:** Does anything in the component `innerHTML` untrusted content? Shadow DOM is not XSS protection — sanitize inputs the same way you would in light DOM.
- **Cost / performance:** Do you use `adoptedStyleSheets` for high-count components? Have you measured upgrade cost on a page with the realistic max element count?
- **Bundling:** Is the package `"type": "module"`, marked `"sideEffects": false`, and does each component have an independent entry so consumers can tree-shake?
- **Distribution:** Is the tag name namespaced (`acme-*`) and is the major version part of the tag if breaking changes are expected?
- **Onboarding:** Can a new engineer find the "define once, guard against double-define" convention in your codebase in under five minutes? This is the most common footgun for newcomers.
- **SSR:** If you ship Declarative Shadow DOM, is there a fallback for Firefox <123 users still in your analytics tail, and is hydration mismatch detected somehow?

## How this topic typically evolves in a codebase

Teams almost always start with **one component** — a widget that had to work in two framework environments, or a widget that had to survive an unknown host page. Someone writes it in vanilla `HTMLElement` in an afternoon and it goes live. It works. Six months later there are five such components, each in a different style, and someone proposes standardizing on Lit. This is the first inflection point: pure vanilla scales badly past ~10 components because change detection, templating, and typed props are re-implemented in each one.

The second inflection is **framework interop debt**. React 18 attribute stringification bugs pile up, and the team either wraps everything with `@lit/react` or pins to React 19. Around the same time, SSR becomes a requirement (SEO team files a ticket), and Declarative Shadow DOM has to be adopted end-to-end — which forces state to be serializable into attributes, which forces API redesign on the older components.

The painful migration point is **v1 → v2 of the design system**. Because tag names are global, you cannot ship two majors under the same name. Teams either version the tag (`acme-button-v2`), rename the package and gate rollouts by consumer, or wait for scoped Custom Element Registries (still not universally shipped in 2026). Whichever path is chosen, plan for it before the first `customElements.define('acme-button', …)` ships — retrofitting versioning after the fact is where most Web Component design systems lose 3-6 months of platform-team time.

## Further reading

- [Lit documentation](https://lit.dev/docs/) — the canonical modern authoring library; the guides double as the best "how to actually build a component" reference for any framework.
- [custom-elements-everywhere.com](https://custom-elements-everywhere.com/) — automated interop test matrix; check before you promise support for a framework version.
- [Open Web Components recommendations](https://open-wc.org/guides/) — testing, publishing, and linting conventions that most production Web Component teams converge on.
- [Google web.dev — Declarative Shadow DOM](https://web.dev/articles/declarative-shadow-dom) — the definitive walkthrough of DSD, hydration, and the polyfill story.
- [Manuel Rauber / Justin Fagnani — "The Cost of Custom Elements"](https://justinfagnani.com/) — pragmatic performance notes from a Lit maintainer on upgrade cost and stylesheet strategies.
- [WHATWG HTML §4.13 Custom Elements](https://html.spec.whatwg.org/multipage/custom-elements.html) — read at least the reactions queue section once; explains half the "why does my callback fire twice" questions.
