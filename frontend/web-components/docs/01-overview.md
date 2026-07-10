# Web Components — Overview

> Web Components are the browser's built-in way to define your own HTML tags — reusable, self-contained UI blocks that work in any framework or in no framework at all.

## The 30-second version

A Web Component is a custom HTML element you invent (say, `<user-card>`) that ships with its own private DOM and its own private CSS. You wire up the behavior in a JavaScript class, register the tag with the browser, and from then on `<user-card email="a@b.com"></user-card>` works everywhere HTML works — React, Angular, Vue, a plain `.html` file, a Rails view. It exists because the industry got tired of rewriting the same button, modal, and date-picker three times per framework generation.

## The mental model

Think of a Web Component as a **sealed appliance**, like a microwave. From the outside it has a clean interface: a few buttons (attributes), a power cord (events), and a slot where you put your food (child content). Inside, it has its own wiring, its own metal casing, its own logic — and crucially, the kitchen's electrical system cannot reach in and rearrange the magnetron. Your page's global CSS cannot bleed in and repaint the microwave's display. The microwave's internal wiring cannot short-circuit your toaster.

That sealing is the **Shadow DOM**. The custom tag name and lifecycle hooks are the **Custom Element**. The pre-baked internal layout you stamp out is the **HTML Template**. Put together, you get a component with the same isolation guarantees a React component wishes it had — but shipped by the browser, no bundler required.

A minimal example:

```html
<script>
  class HelloBadge extends HTMLElement {
    connectedCallback() {
      this.attachShadow({ mode: 'open' }).innerHTML = `
        <style>span { color: tomato; font-weight: 600; }</style>
        <span>Hello, <slot>friend</slot>!</span>
      `;
    }
  }
  customElements.define('hello-badge', HelloBadge);
</script>

<hello-badge>Quang</hello-badge>
```

That `<hello-badge>` is now a real HTML element. Drop it into any page, any framework, forever.

## What it is NOT

- **Not a framework.** React, Vue, Svelte give you rendering, state, routing. Web Components give you one primitive: a custom tag with encapsulation.
- **Not iframes.** Iframes are process-isolated documents with their own URL and network context. Web Components share the page's JS runtime — they are just scoped DOM.
- **Not React components.** React components are a compile-time abstraction the browser never sees. Web Components are real DOM nodes the browser understands natively.
- **Not Web Assembly.** WASM runs sandboxed binary code. Web Components are plain JavaScript that produces DOM.

## When you would reach for it

- Building a design system that has to work across React, Angular, and legacy jQuery pages.
- Shipping an embeddable widget (chat bubble, checkout button) that must survive an unknown host site's CSS.
- Micro-frontends where each team picks its own framework but exports a shared tag.
- Adding one interactive island to an otherwise static site without pulling in a framework.

## When you would NOT reach for it

- You are already all-in on one framework and never plan to leave — the framework's own component model has better ergonomics, better dev tooling, and better server-rendering support.
- You need heavy SSR/hydration with fine-grained streaming. Declarative Shadow DOM helps but the ecosystem is still thinner than Next.js or SvelteKit.
- The component is trivial and used only once. A plain function or partial is lighter.

## Key vocabulary (just enough to keep reading)

- **Custom Element** — a class extending `HTMLElement` registered under a hyphenated tag name.
- **Shadow DOM** — an encapsulated DOM subtree attached to an element; styles and queries do not cross the boundary.
- **HTML Template** — a `<template>` tag whose contents are parsed but inert until cloned.
- **Slot** — a placeholder inside the shadow tree where the host's light-DOM children get projected.
- **Light DOM** — the children you write between the opening and closing custom tag, visible to the outside page.
- **Lifecycle callbacks** — `connectedCallback`, `disconnectedCallback`, `attributeChangedCallback`, `adoptedCallback`.
- **`customElements.define`** — the browser API that ties a tag name to a class.
- **Declarative Shadow DOM** — server-rendered shadow trees via `<template shadowrootmode="open">`, no JS needed for first paint.

## What's next

The next document, `02-deep-dive.md`, answers the What / Where / When / How / Why in detail: the three specs individually, styling boundaries, form-associated elements, SSR, and how Web Components interoperate with modern frameworks.
