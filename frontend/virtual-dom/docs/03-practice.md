# Virtual DOM — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In a typical SaaS frontend, the Virtual DOM is the invisible thing between your `setState` and the pixels. You don't think about it until a list jitters during reorder, an input loses focus on every keystroke, or the Performance tab shows 80 ms of scripting per click — at which point the topic is suddenly the entire job.

The recurring shapes are: long lists with reordering (kanban boards, inbox views, query result tables), form-heavy screens where every keystroke causes a parent re-render, dashboards that fan out the same context to dozens of widgets, and editor-like UIs (rich text, canvas overlays, drag-and-drop) where VDOM ownership collides with imperative DOM state. If you work on any of these, the rest of this document is your day-to-day.

## Best practices

### 1. Key list items by stable identity, never by array index
**Do:** Use the item's own ID — `key={user.id}`, `key={message.uuid}`, `key={`${row.id}-${col.id}`}` for grids. If your data has no ID, generate one at *creation* time (not render time) and store it with the item.
**Why:** The diff matches children by key. Index keys silently work until rows reorder, insert at the head, or filter — then DOM state (input value, focus, CSS animation, `<video>` playback position) follows the *position* instead of the *item*. This is the single most common reconciliation bug.
**Avoid:** `key={i}`, `key={Math.random()}`, `key={JSON.stringify(item)}`. The first lies, the second forces unmount/remount every render, the third is slow and brittle.

```jsx
// Bad — type into row 2, delete row 1, your text is now in row 1.
{rows.map((row, i) => <Row key={i} row={row} />)}

// Good — text follows the row because identity is stable.
{rows.map(row => <Row key={row.id} row={row} />)}
```

### 2. Stabilize prop references before reaching for `memo`
**Do:** Wrap callbacks in `useCallback`, derived objects/arrays in `useMemo`, and hoist literals out of render. Then — and only then — wrap the receiving child in `React.memo`. In Vue 3, prefer `shallowRef` for big objects you replace by reference (chart data, editor docs).
**Why:** `React.memo` does a shallow prop comparison. If the parent re-renders and passes `{ style: { color: 'red' } }` or `onClick={() => …}`, the reference is new every render, equality fails, and `memo` does nothing except cost you a comparison.
**Avoid:** Sprinkling `memo` on every component "just in case" without checking what props you're actually handing it. Premature memo is just dead code with a benchmark cost.

### 3. Lift state *down*, not up, when only a leaf cares
**Do:** If only `<SearchBox>` needs the query string, keep `useState` inside `<SearchBox>`, not in the page-level component. Split a fat parent into two children that own their own slices.
**Why:** A re-render of a parent re-renders every child whose memoization fails. Keeping state at the leaf turns a 200-component re-render into a 1-component re-render with no memo gymnastics.
**Avoid:** Hoisting every piece of state to the top "because it's cleaner." It centralizes complexity and broadcasts re-renders.

### 4. Treat context values as immutable identities
**Do:** Memoize the `value` you pass to a `Provider`. Split unrelated state into separate contexts (one for theme, one for auth, one for current user) instead of one mega-context.
**Why:** Every consumer of a context re-renders when the provider's value identity changes. A new `{ user, theme, settings }` object literal on every parent render fans out to every consumer in the tree.
**Avoid:** `<Ctx.Provider value={{ user, setUser }}>` inline. That object is fresh every render.

### 5. Read the Profiler before optimizing
**Do:** Open React DevTools Profiler, enable "Record why each component rendered", run the interaction, then look at the flamegraph. Wide warm-colored bars are slow renders; gray bars are components that *did not* re-render. The ranked chart sorts the same data by cost.
**Why:** Memoization decisions made without measurement are almost always wrong — humans guess the wrong component. The Profiler tells you which prop's identity changed and which subtree to actually fix.
**Avoid:** Optimizing components you have not measured. In Vue 3, use Vue DevTools' Performance tab the same way; look for components highlighted on every update that shouldn't be.

### 6. Use `v-memo` and `shallowRef` deliberately in Vue 3
**Do:** Reach for `v-memo` on large `v-for` lists whose items rarely change (`v-memo="[item.id, item.updatedAt]"`). Use `shallowRef` when wrapping objects you mutate by replacement — chart datasets, Monaco editor instances, large parsed ASTs.
**Why:** Vue's runtime is already cheap because of patch flags, but big lists and deeply nested reactive proxies are the two places where it still hurts. `shallowRef` skips reactivity recursion entirely below the root.
**Avoid:** Wrapping every ref in `shallowRef` "for performance." You will lose reactivity on mutations and spend an afternoon debugging why the UI doesn't update.

### 7. Virtualize lists past a few hundred rows
**Do:** Use `react-window`, `react-virtuoso`, or `vue-virtual-scroller` when row count exceeds the viewport by more than ~3x. The diff is O(n), but n=10,000 still allocates 10,000 vnodes per render.
**Why:** VDOM cost is per-node, not per-pixel. Virtualization caps n at "what's visible" and turns scroll into recycling 20 nodes instead of rendering 10,000.
**Avoid:** Building your own "render only what's visible" with `scrollTop` listeners. Edge cases (variable row height, keyboard nav, screen readers) eat months.

### 8. Escape the VDOM with refs for imperative APIs, not to "just fix" rendering
**Do:** Use `ref` when the browser API is imperative by nature: `input.focus()`, `element.scrollIntoView()`, `videoRef.current.play()`, attaching CodeMirror or Chart.js to a `<div>`. For layered UI (modals, tooltips, toasts), use `createPortal` so the node renders into `document.body` while React still owns its lifecycle.
**Why:** Refs are the sanctioned escape hatch. Portals keep stacking-context bugs out of your tree without breaking event bubbling or context.
**Avoid:** Calling `ReactDOM.flushSync` to "force a render," mutating DOM children that React also renders, or stashing DOM nodes you appended yourself inside a React-managed parent. The next reconciliation will overwrite or orphan them.

### 9. Don't define components inside other components
**Do:** Declare every component at module scope. If you need closure over props, pass them in.
**Why:** `function Parent() { function Row() {…}; return <Row/> }` creates a *new function identity* for `Row` on every parent render. React sees a different `type`, tears down the entire subtree, and remounts it. State, focus, and animations all reset.
**Avoid:** Inline component declarations inside JSX, render props that close over `useState` setters without `useCallback`, and HOCs applied inside a render.

### 10. Know when to leave the VDOM behind
**Do:** For input-heavy or animation-heavy surfaces (financial trading grids, audio meters, real-time canvas overlays, large form builders), seriously evaluate Solid, Svelte 5, or Vue Vapor Mode. They skip the diff in the steady state and update only the bound DOM nodes.
**Why:** VDOM has a fixed per-render floor — allocate vnodes, walk the tree, compare props. At 120 Hz on a 5000-node screen, that floor becomes the bottleneck no amount of `memo` can fix.
**Avoid:** Rewriting a CRUD dashboard in Solid to chase 2 ms benchmarks. Most apps are ergonomics-bound, not VDOM-bound. The rewrite is only worth it when the Profiler shows the reconciler itself, not your code, as the long pole.

## Anti-patterns to recognize

- **`key={Math.random()}` or `key={Date.now()}`**: looks like it "gives every item a unique key"; actually forces unmount + remount every render, destroying state and animations. Use the item's own ID instead.
- **New object literal in a memoized child's props**: `<Child style={{margin: 8}} options={{}} />` next to `React.memo(Child)`. Memo never hits; everything re-renders. Hoist the literal out of render or `useMemo` it.
- **Context as a state-management bus**: one Provider with a giant `{ ...everything }` value re-renders every consumer on any change. Split contexts by update frequency and audience.
- **Conditional element type**: `{condition ? <div>…</div> : <section>…</section>}` around the same children. Type change forces a full unmount of the subtree. Wrap the conditional inside the element, not around it.
- **Calling `forceUpdate` or `flushSync` to "make it render now"**: nearly always covers for state that should live in React (or Vue's reactivity) and doesn't. Move the state in; remove the force.
- **Direct DOM mutation on React-managed nodes**: `document.querySelector('.row').classList.add(…)` inside a component that React also renders. The next reconciliation overwrites you. Use class props or refs scoped to *your* element.
- **Defining a component inside another component's body**: looks tidy; remounts the whole subtree every parent render. Hoist it.
- **Spreading unknown props all the way down**: `<Wrapper {...props}>` with no shape contract — every parent prop change reaches every leaf, and memoization is impossible because identities you don't own keep changing.

## Real-world usage patterns

- **Inbox-style list (email, chat, support tickets), 5–50k items.** The team key-by-message-ID, virtualize with `react-virtuoso`, and memoize row components on `(message.id, message.isRead, message.updatedAt)`. Non-obvious lesson: the row's `onClick` handler must be stable (`useCallback` *or* event delegation on the parent) or `memo` falls over and the whole list re-renders on every selection.

- **Realtime dashboard with 30+ widgets updating from a WebSocket.** Each widget subscribes to its own slice via a selector hook (Zustand/Redux Toolkit/Pinia) rather than reading one global context. Non-obvious lesson: keep the *transport* (WebSocket handler) outside the React tree; let it write to the store, and let components read. Putting the socket inside a top-level component re-renders the world on every tick.

- **Drag-and-drop kanban board.** Cards use stable IDs as keys, columns use a portal for the drag preview so it can escape `overflow: hidden`. While dragging, the team uses `useDeferredValue` (React 18+) on the destination index so the heavy column re-render doesn't fight the 60 fps animation. Non-obvious lesson: optimistic reordering must commit a new array with new positions but the same item references, or memoization breaks downstream.

- **Form builder with 200+ live inputs.** Each field owns its own `useState`; the form-level "value" is assembled on submit via refs or a form library (React Hook Form, VeeValidate) that bypasses controlled-input re-renders. Non-obvious lesson: a single top-level controlled form re-renders every field on every keystroke. Local state at the leaf is the cheapest possible memoization.

- **Trading grid pushing 100+ updates/second.** After exhausting memoization, the team replaced the grid with a Solid island inside an otherwise React app. Non-obvious lesson: you don't have to pick one framework for the whole app — render the hot surface with fine-grained reactivity and keep the chrome in React. The boundary is a single `<div ref={mountSolid}>`.

## Operational checklist

- Lists: every `.map(...)` has a stable, non-index key derived from data identity.
- Profiler: a recent recording exists for the screen's primary interaction; warm bars in the flamegraph have a known reason.
- Memoization: every `React.memo` has been verified to actually skip renders (Profiler shows it gray on unrelated updates).
- Context: each Provider's `value` is memoized; mega-contexts are split by audience.
- Virtualization: any list expected to exceed a few hundred items is virtualized, with row height assumptions documented.
- Refs vs render: imperative DOM work (focus, scroll, third-party widgets) goes through refs, never through `flushSync` hacks.
- Portals: overlay UI (modals, tooltips) uses portals; event bubbling and focus traps are tested.
- Effects: no `useEffect` writes state that the same effect depends on (reconciliation loop).
- Onboarding: new engineers know how to open the Profiler, read a flame graph, and find the prop whose identity changed.

## How this topic typically evolves in a codebase

Teams almost always start by ignoring the VDOM entirely. State lives at the top, callbacks are inline, props are object literals, lists are keyed by index. It works because the app is small and React/Vue is fast enough. Then the screen hits ~50 components or one list crosses a few hundred rows, and the first "this feels janky" ticket lands.

The middle phase is *reactive memoization*: someone discovers `React.memo` and `useCallback`, sprinkles them across the codebase, and gets a partial win. This is usually where bad keys, fresh object literals, and mega-contexts are flushed out by the Profiler. It's also where the team learns that memoization without measurement makes the codebase noisier and barely faster.

The mature phase is *architectural*: state colocated with the components that read it, a store with selector hooks for genuinely shared state, virtualization on long lists, and a deliberate choice between VDOM and fine-grained reactivity for the hot spots. The painful migration point is usually moving from "one big context / one big form state" to selectors — it touches every consumer. With React 19's compiler and Vue's Vapor Mode shipping, expect the next migration to be removing hand-written `useMemo`/`useCallback` once you trust the compiler to do it for you.

## Further reading

- [React docs — "Rendering Lists" and "You Might Not Need an Effect"](https://react.dev/learn/rendering-lists) — the modern canonical guidance on keys and on avoiding effect cascades; both fix the most common VDOM bugs at the source.
- [Vue.js — Performance guide](https://vuejs.org/guide/best-practices/performance) — official, opinionated coverage of `v-memo`, `shallowRef`, and what the Vue 3 compiler already does for you.
- [Introducing the React Profiler (Brian Vaughn, React blog)](https://legacy.reactjs.org/blog/2018/09/10/introducing-the-react-profiler.html) — still the clearest explanation of what the flamegraph and ranked chart actually show.
- [Mark Erikson — "A (Mostly) Complete Guide to React Rendering Behavior"](https://blog.isquaredsoftware.com/2020/05/blogged-answers-a-mostly-complete-guide-to-react-rendering-behavior/) — the single best deep article on why components re-render in practice.
- [Ryan Carniato — "Components are Pure Overhead"](https://dev.to/this-is-learning/components-are-pure-overhead-hpm) — the case for fine-grained reactivity over VDOM, from the author of SolidJS. Read it before you decide to leave the VDOM.
- [React Compiler docs](https://react.dev/learn/react-compiler) — what auto-memoization actually does, what it doesn't, and how to verify it ran on your component.
