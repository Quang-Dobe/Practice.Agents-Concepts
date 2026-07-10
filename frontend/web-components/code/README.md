# Web Components — MVP Code

The smallest runnable demo of a Web Component: `<counter-button>`. About 60 lines of actual code, comments excluded. Zero dependencies — raw `HTMLElement`, no Lit, no framework.

## What it demonstrates

- **Custom Element registration** with a hyphenated tag name, guarded against duplicate `define()`.
- **Shadow DOM encapsulation** — internal styles cannot leak out; outer styles cannot select `button` inside. Consumers reach in only through `::part(button)`.
- **Attribute reflected to a property** — `count` stays in sync whether set via markup, CSS attribute selector, or `el.count = …` from JS. `observedAttributes` gates the `attributeChangedCallback` firings.
- **Slot projection** — the light-DOM text between the tags renders inside the shadow tree via the default `<slot>`.
- **Typed CustomEvent across the shadow boundary** — dispatched with `composed: true` so a `document`-level listener actually receives it, and typed via `HTMLElementEventMap` merging so `event.detail` needs no cast.

## Prerequisites

- **Node 20+** (for `tsc` and a static server).
- **TypeScript 5.4+** globally or via `npx`.
- A local static server: `npx serve` or `python -m http.server`.

## Run it

```bash
# 1. Compile TS -> JS (produces mvp.js next to mvp.html)
npx -p typescript@5.4 tsc mvp.ts --target es2022 --module esnext --moduleResolution bundler --strict

# 2. Serve the folder (either works)
npx serve .
# or: python -m http.server 8000

# 3. Open http://localhost:3000/mvp.html (or :8000/mvp.html)
```

## Expected output

Two buttons rendered as `[ Increment me ] 0` and `[ Starts at 10 ] 10`. Click either and the number ticks up; the log line below reads e.g. `counter-change from <counter-button> -> count=1 delta=1`. The second button starts at 10 and uses a dashed ghost style — proving the same tag renders differently based on attributes.

## What to try next

- Delete `composed: true` from the CustomEvent init and observe the document-level listener stop firing.
- Remove `'count'` from `observedAttributes` and watch the display freeze at 0 even though the attribute changes.
- Add `<style>button { background: red }</style>` in `mvp.html` — the shadow button is untouched. Then swap it for `counter-button::part(button) { background: red }` and see the difference.
- Wrap one `<counter-button>` in a `<div>` and add the listener there instead of on `document` — the bubble path still works because the event is `composed`.
