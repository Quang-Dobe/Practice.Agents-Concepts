# Web Components — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

"Web Components" is not a single spec — it is a **suite of three W3C/WHATWG specifications** that together allow authors to define, isolate, and instantiate DOM subtrees as reusable custom HTML elements:

1. **Custom Elements v1** — WHATWG HTML Standard §4.13 (`html.spec.whatwg.org/multipage/custom-elements.html`). Defines `customElements.define()`, the reaction queue, and the four lifecycle callbacks.
2. **Shadow DOM v1** — WHATWG DOM Standard §4.8, integrated with the HTML parser. Defines `attachShadow()`, `ShadowRoot`, event retargeting, and the composed tree.
3. **HTML `<template>`** — WHATWG HTML Standard §4.12.3. Parses to an inert `DocumentFragment` on `content` that is not rendered and whose scripts do not execute.

The three combine into what MDN calls "the Web Components APIs" — a set of primitives whose composition yields encapsulated, framework-agnostic UI units.

### The core building blocks

- **`HTMLElement` subclass** — every autonomous custom element is a class extending `HTMLElement`. The constructor runs during element creation; DOM mutation inside the constructor is illegal (spec throws `NotSupportedError`).
- **`CustomElementRegistry`** — the global `window.customElements` object; owns `define(name, ctor, options?)`, `get()`, `whenDefined()`, and `upgrade()`. A tag can only be registered once and the name must contain a hyphen (`x-foo`, not `foo`).
- **Shadow root** — a `DocumentFragment`-like node returned by `element.attachShadow({ mode })`. The subtree it hosts is the *shadow tree*; the element's original children are the *light tree*. The composed tree is what the renderer paints.
- **`<slot>`** — a placeholder in the shadow tree that projects light-DOM children into a specific spot. Slots can be **default** (`<slot>`) or **named** (`<slot name="header">`).
- **`<template>`** — inert markup container; `template.content` is a `DocumentFragment` you `cloneNode(true)` to stamp instances cheaply.
- **`ElementInternals`** — obtained via `this.attachInternals()`. Exposes form association, ARIA reflection, and custom validity (WebKit shipped this in 16.4; see webkit.org/blog/13711).

### How it relates to the broader landscape

Web Components sit in the family of **encapsulated UI primitives**. Sibling technologies are framework components (React, Vue, Svelte, Solid), which are compile-time abstractions the browser never sees, and iframes, which are process-level isolation with their own document. The unique niche of Web Components is *runtime, standards-based, cross-framework* encapsulation — the browser itself understands the tag, so a `<my-widget>` inside React, Vue, and a static `.html` all render the same DOM.

## Where

### Where it lives in the stack

Purely **client-side**, at the DOM layer — one rung above the parser, one rung below any framework's virtual DOM. A custom element is a real `Node` in the document tree; the browser dispatches events to it, applies CSS to its host box, and calls its lifecycle callbacks synchronously as part of the DOM mutation algorithm. Server-rendered variants exist via **Declarative Shadow DOM** (a `<template shadowrootmode="open">` child that the parser converts into a shadow root during HTML parsing — see web.dev/articles/declarative-shadow-dom), which moves first-paint into the HTML byte stream.

### Where you typically encounter it

- **GitHub.com** — heavy internal use for widgets like `<relative-time>`, `<clipboard-copy>`, `<details-menu>`.
- **YouTube** desktop — built on the Polymer library, still ships hundreds of `yt-*` custom elements.
- **Salesforce Lightning Web Components (LWC)** — an entire enterprise app framework whose components are literal custom elements.
- **Adobe Spectrum Web Components**, **Microsoft Fluent UI Web Components**, **SAP UI5 Web Components** — vendor design systems shipped as tag libraries.
- **Ionic Framework** — the same `<ion-button>` renders identically in Angular, React, and Vue projects.
- **Shoelace / Web Awesome** — general-purpose UI kit that works with any (or no) framework.

### Ecosystem and tooling

- **For authoring**: [Lit](https://lit.dev) (2.x/3.x, Google, ~5 KB), [Stencil](https://stenciljs.com) (Ionic, compiles TSX to custom elements), [FAST](https://www.fast.design) (Microsoft), plain vanilla.
- **For SSR / hybrid**: `@lit-labs/ssr`, Enhance, WebC (11ty), Astro's built-in custom-element support.
- **For testing**: `@open-wc/testing`, Web Test Runner, Playwright (native).
- **For interop**: `@lit/react` (wraps a custom element as a React component with typed props/events), Angular's `CUSTOM_ELEMENTS_SCHEMA`, Vue's `compilerOptions.isCustomElement`.
- **Specs and reference**: WHATWG HTML §4.13, DOM §4.8, MDN Web Components section, `custom-elements-everywhere.com` (interop test matrix).

## When

### When the topic emerged and why

Web Components began around 2011 as a Google project (Alex Russell, Dimitri Glazkov) crystallized into the original v0 specs. v0 shipped only in Chrome and had ergonomic problems: `document.registerElement`, callback names in `createdCallback` style, and Shadow DOM v0's `<content>` selector. **Custom Elements v1** and **Shadow DOM v1** were re-designed in 2016, driven by cross-vendor consensus. Safari shipped v1 in 2016 (WebKit blog, "Introducing Custom Elements"), Firefox in 2018 with release 63, Edge switched to Chromium in 2020 and inherited support. The pre-existing pain the specs solved: every framework re-inventing the same scoping model, and the industry losing components every time a framework generation turned over (Backbone views → Angular 1 directives → React 15 mixins → …).

### When to use it in a project

Reach for it when:

- The same interactive widget must ship into **multiple framework contexts** or unknown host pages (embeds, checkouts, chat widgets).
- You are building a **design system** with a >3-year expected lifespan across teams that will not agree on one framework.
- You need **hard style isolation** — third-party CSS on the page must not touch your widget.
- You want a **framework-free island** on an otherwise static page and refuse to add a bundler.
- The team is comfortable with **DOM APIs** and does not need JSX-level ergonomics for internal state.

### When NOT to use it

Avoid it when:

- The app is single-framework and staying single-framework. You will fight the framework's idioms (React data binding, Vue reactivity) at every boundary.
- You need **rich SSR with streaming and fine-grained hydration**. Declarative Shadow DOM plus `@lit-labs/ssr` gets you 80%, but Next.js / Remix / SvelteKit are ahead.
- You need to **pass complex objects as attributes**. Attributes are strings; you need properties, and that is where framework interop breaks.
- The team is small, junior, and moving fast. Vanilla Web Components require you to hand-build change detection, templating, and state — a framework does that.

## How

### How it works under the hood

The lifecycle of a custom element from source HTML to torn-down node:

1. **Registration** — script calls `customElements.define('x-foo', XFoo)`. The registry validates the name (must contain `-`, be lowercase, and not be a reserved name like `annotation-xml` per WHATWG HTML §4.13.1), stores `{ name → constructor }`, and schedules an *upgrade* for every already-parsed `<x-foo>` currently in any document that uses this registry.
2. **Parsing** — the HTML parser encounters `<x-foo>`. If the tag is undefined at that moment, the parser creates a placeholder `HTMLElement` with the correct tag but no custom behavior; it matches the `:not(:defined)` pseudo-class.
3. **Upgrade** — when `define()` is called later, or now if already defined, the browser walks the placeholder, invokes the constructor with `new.target === XFoo`, swaps the prototype, and enters the *custom element reactions* queue.
4. **`connectedCallback`** — fires when the element is inserted into a document that has a browsing context. Called *every* time it is (re)inserted, including on `appendChild` moves. Not idempotent — check `this.isConnected`.
5. **`attributeChangedCallback(name, old, new, namespace)`** — fires only for attributes named in the static `observedAttributes` getter. The browser skips other attribute mutations as a performance optimization (web.dev/articles/custom-elements-v1). Runs synchronously inside the mutation algorithm.
6. **`adoptedCallback(oldDoc, newDoc)`** — fires on `document.adoptNode()`, e.g. moving between an iframe and the main document. Rare in practice.
7. **`disconnectedCallback`** — fires on removal from the tree. Not called on page unload; use it for teardown of listeners, timers, `ResizeObserver`s.
8. **Shadow attachment** — usually inside the constructor: `this.attachShadow({ mode: 'open', delegatesFocus: false, slotAssignment: 'named' })`. `mode` is permanent for the life of the element.
9. **Rendering** — the browser flattens light DOM into slots to build the *composed tree*, then paints. Slotted nodes stay in the light DOM for querying but visually appear inside the shadow tree.
10. **Event flow** — events dispatched inside the shadow tree bubble through the composed path. As they cross the shadow boundary, their `target` is **retargeted** to the shadow host (unless the listener itself lives inside the same shadow tree). `event.composedPath()` returns the full path only if the shadow root is `open`; a `closed` root elides itself and its descendants (see `pm.dartus.fr/posts/2021/shadow-dom-and-event-propagation`). Events must be dispatched with `composed: true` to escape the shadow tree at all.

Waiting for definitions without a race:

```js
await customElements.whenDefined('x-foo');
// The class is registered globally now.
// Any existing <x-foo> in the DOM has been (or is being) upgraded.
```

### Key trade-offs

| Choice | Gain | Give up |
|---|---|---|
| Open shadow root | Devtools introspection, testable via `el.shadowRoot`, `composedPath()` works | No true encapsulation — anyone with a reference can crawl inside |
| Closed shadow root | External code cannot reach `shadowRoot`; events hide their internal path | Your own tests, a11y tools, and framework wrappers also lose access. Not a security boundary. |
| Autonomous element (`class X extends HTMLElement`) | Cross-browser, clean semantics | You re-implement built-in behavior (focus, ARIA) |
| Customized built-in (`class X extends HTMLButtonElement`, `<button is="x-btn">`) | Inherits accessibility and semantics of the base tag | **Safari/WebKit refuses to implement** (bugs.webkit.org/show_bug.cgi?id=182671, still open in 2026); requires a polyfill |
| Shadow DOM styling | Guaranteed isolation, `:host`, `::part`, `::slotted` | Global design tokens must come through CSS custom properties (`--x`) which *do* pierce shadow boundaries |
| Slots | Cheap composition, host keeps ownership of children | Slotted nodes cannot be selected past their first level (`::slotted(a b)` is invalid — simple selectors only, per CSS Scoping §3.3) |
| Attributes as API | Serializable, HTML-native, work with SSR | Only strings; complex data needs properties |
| Properties as API | Any JS value, no serialization cost | Invisible to HTML, must be set from JS after upgrade |

### The style boundary in detail

- CSS rules defined *inside* the shadow root do not select light-DOM nodes, and outer CSS rules do not select shadow nodes.
- **CSS custom properties (variables) inherit through the boundary** — this is the officially blessed theming channel.
- `::part(name)` lets the shadow author explicitly expose named internals: `<button part="submit">` becomes stylable via `my-widget::part(submit) { ... }`. Only simple selector fragments allowed on the outside; you cannot descend past the part.
- `::slotted(selector)` inside shadow CSS targets *distributed* light-DOM children. Simple selector only; matches only direct children of the slot (MDN: `developer.mozilla.org/en-US/docs/Web/CSS/::slotted`).

### Framework interop specifics

- **React** — historically React set every unknown prop as an *attribute*, stringifying objects and losing booleans. **React 19** finally implements the RFC (facebook/react#11347): if a matching property exists on the element instance, React assigns it as a **property**; otherwise it falls back to an attribute, with boolean handling (`add/remove`). Functions become event listeners with the `on…` convention on lowercase custom events. Complex non-primitive values that miss a property may still stringify — a known gotcha (facebook/react#29037).
- **Vue 3** — set `compilerOptions.isCustomElement: tag => tag.startsWith('x-')`. Vue writes properties by default and attributes with `.attr` modifier.
- **Angular** — add `CUSTOM_ELEMENTS_SCHEMA` to the module. Property/event binding via `[prop]` and `(event)` work natively.
- **Solid, Svelte, Preact, Lit** — all score green across the board on `custom-elements-everywhere.com`.

### SSR and Declarative Shadow DOM

Historically shadow roots existed only after `attachShadow()` ran, meaning SSR could not deliver a first paint with encapsulated styles. **Declarative Shadow DOM** fixes this:

```html
<my-card>
  <template shadowrootmode="open">
    <style>h2 { color: tomato; }</style>
    <h2><slot></slot></h2>
  </template>
  Hello
</my-card>
```

The HTML parser lifts the `<template>` into a shadow root at parse time — no JS needed. Support: Chrome 111+, Edge 111+, Safari 16.4+, Firefox 123+ (early 2024), Baseline "Newly available" since 2024-02-20, expected Baseline Widely Available in mid-2026 (see `web-platform-dx.github.io/web-features-explorer/features/declarative-shadow-dom/`). SSR pitfalls that persist: `fetch()`-based data cannot be awaited in a constructor; state has to be serialized into attributes; and hydration mismatch is silent by default because there is no framework-level diff.

### Common failure modes

- **`this` before super** — touching `this` in the constructor before `super()` throws `ReferenceError`. Consequence of extending `HTMLElement`.
- **DOM work in constructor** — the spec forbids it; browsers throw `NotSupportedError` if you attempt to add children before `connectedCallback`.
- **`connectedCallback` fires more than once** — moving the element via `appendChild` runs disconnect + reconnect. Idempotency required.
- **`attributeChangedCallback` never fires** — you forgot the static `observedAttributes` getter.
- **Attribute vs. property drift** — the outside sets `el.checked = true`, the reflected attribute doesn't update, the framework re-renders and clobbers state. Cause: no reflection wiring.
- **Style leaks via `<slot>`** — slotted content lives in the light DOM, so global CSS still styles it. Not a bug, but often surprising.
- **Closed roots and a11y tools** — some screen readers historically failed to reach into closed shadow roots; use open unless you have a threat model.
- **FOUC before upgrade** — undefined elements render as unstyled inline boxes. Fix with the `:not(:defined) { visibility: hidden }` pattern.
- **Global registry collision** — two copies of the same library both call `define('x-foo', …)`; the second throws. Scoped Custom Element Registries (spec in progress) will address this.

## Why

### Why it exists

Because the browser is the only truly durable runtime on the web, and every previous attempt at reusable UI (Backbone views, Dojo widgets, Angular 1 directives, React components) died with its framework. The specs address three first-principles concerns: **encapsulation** (isolate styles and DOM so composition scales), **interoperability** (a plain HTML tag is understood everywhere), and **longevity** (standards outlive frameworks).

### Why it looks the way it does

Two design choices look strange until you see the alternative.

*Hyphen-required tag names.* The obvious alternative is a registry with reserved words. The committee chose the hyphen rule so that the HTML parser can decide at parse time — with zero lookup — whether a tag is potentially custom, and reserve the un-hyphenated space forever for future built-in elements. Cheap, forward-compatible, no ambiguity.

*Shadow DOM instead of CSS modules.* A CSS-only scoping system (à la Vue's `<style scoped>`) would have shipped faster and been simpler. It was rejected because it does not scope DOM queries — `document.querySelector('button')` would still enter the widget. Shadow DOM gives you a genuine tree boundary, at the cost of a heavier mental model and one extra concept (composed vs. flat tree).

### Why it matters now

In 2026 the ecosystem has crossed two important lines. **React 19 finally understands custom elements**, removing the last major framework-interop excuse. **Declarative Shadow DOM has reached all evergreen browsers**, closing the SSR gap that once made Web Components a poor fit for content-first sites. Meanwhile, design-system consolidation (Adobe Spectrum, Fluent, SAP UI5, GitHub Primer's ongoing migration) has quietly made custom elements the default currency of cross-framework UI at large companies. The technology is neither novel nor niche — it is the browser's answer to the component question, now stable enough to bet on.

## Open questions / things to verify in practice

- Measure upgrade cost on a page with 1000+ custom elements — how long does the browser spend in reactions vs. layout?
- Confirm `event.composedPath()` behavior with your framework's synthetic event system. React's does not perfectly mirror the native composed path.
- Test how `::part` interacts with your design-token system when parts are nested inside other custom elements — does the outer selector still reach?
- Verify Declarative Shadow DOM interop with your SSR framework and check whether the polyfill for older Firefox is needed for your traffic.
- Confirm whether your React 19 code path is assigning object props as properties or as attributes; log a complex object prop and inspect the resulting DOM.
- If you need customized built-ins (`is=""`), audit Safari traffic — WebKit still refuses to ship it as of 2026.
