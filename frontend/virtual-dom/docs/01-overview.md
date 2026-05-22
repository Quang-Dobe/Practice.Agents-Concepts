# Virtual DOM — Overview

> The Virtual DOM is a lightweight JavaScript copy of your UI tree that a framework diffs against the previous copy so it can patch the real DOM with the smallest possible set of changes.

## The 30-second version

The browser's DOM is a live, stateful tree. Touching it — creating nodes, setting attributes, reading layout — is expensive, and naive UI code touches it constantly. The Virtual DOM (VDOM) is the idea of describing what the UI *should* look like as plain JS objects, letting the framework compare the new description to the old one, and only then writing the difference to the real DOM. React popularized it; Vue 2/3, Preact, Inferno, and Snabbdom use the same shape of trick.

## The mental model

Imagine you are a teacher with a whiteboard full of equations. A student walks up every few seconds and says "here is what the board should look like now" and hands you a fresh photo. You have two choices.

The lazy choice: erase the entire board and copy the photo from scratch. Correct, but the students in the front row will hate the flicker, and your arm will fall off.

The smart choice: hold the previous photo next to the new one, scan for the parts that actually differ, and only erase and rewrite those. Most of the board stays untouched.

The Virtual DOM is the photo. It is cheap to make (just JS objects in memory), cheap to compare (just walking two trees), and the only expensive step — touching the whiteboard — happens once per update, in a tight batch, for only the cells that changed.

A vnode is just an object:

```js
{ type: 'div', props: { className: 'card' }, children: [
  { type: 'h1', props: {}, children: ['Hello'] }
]}
```

Your `render` function returns one of these trees. The framework keeps the last one it saw, diffs it against the new one, and emits real DOM calls — `createElement`, `setAttribute`, `removeChild` — for the delta. You write declarative code; the framework does the imperative DOM surgery.

## What it is NOT

- Not the real DOM. It is a plain-object mirror; the browser never sees it.
- Not the Shadow DOM. Shadow DOM is a browser feature for style/scope encapsulation in Web Components — unrelated.
- Not automatically faster than hand-written DOM code. A careful imperative update will beat it. VDOM trades peak speed for a sane programming model.
- Not required for reactivity. Svelte and SolidJS skip the VDOM entirely and still get fine-grained updates.

## When you would reach for it

- You are building a UI where state changes ripple across many nodes and you do not want to track which DOM bits to mutate by hand.
- You want a declarative `state -> UI` function and are willing to pay a small diffing cost for it.
- You are picking a framework (React, Vue, Preact) — the VDOM comes with the box.

## When you would NOT reach for it

- You are updating one counter on an otherwise static page — `textContent =` is faster and simpler.
- You need the absolute lowest latency per keystroke (high-frequency canvas/game UIs) — go imperative or use a compiler-based framework like Svelte.
- Your app is server-rendered HTML with sprinkles of interactivity — htmx or vanilla JS is lighter.

## Key vocabulary (just enough to keep reading)

- **DOM**: the browser's live tree of element nodes.
- **VNode**: a plain JS object describing one element (`type`, `props`, `children`).
- **VTree**: a tree of vnodes — what one render pass produces.
- **Diff**: walking old and new vtrees to find what changed.
- **Patch**: the list of real-DOM operations produced by the diff.
- **Reconciliation**: the whole diff + patch process.
- **Key**: a stable identifier on a list item so the diff can match nodes across renders instead of guessing.
- **Mount / Unmount**: first insertion of a vnode into the DOM, and its removal.

## What's next

The next document answers What / Where / When / How / Why in detail — including the heuristics that make diffing O(n) instead of O(n^3), what `key` actually does, and where React Fiber fits in.
