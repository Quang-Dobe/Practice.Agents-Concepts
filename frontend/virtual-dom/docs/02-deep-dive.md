# Virtual DOM — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

A Virtual DOM (VDOM) is an in-memory tree of plain JavaScript objects that mirrors the structure the framework wants the real DOM to take, paired with a **reconciliation** procedure that diffs a new tree against the previously committed tree and emits a minimal sequence of imperative DOM mutations. The VDOM is not a standard — it is a pattern. Each framework picks its own vnode shape, its own diffing heuristics, and its own scheduling layer on top.

The contract is roughly:

```
render(state) -> VTree_new
diff(VTree_old, VTree_new) -> Patch
apply(Patch) -> real DOM mutations
```

### The core building blocks

- **VNode**: a JS object describing one element. At minimum `{ type, props, children }`. React internally calls it a `ReactElement`; Vue calls it a `VNode`; Snabbdom calls it `VNode` too.
- **VTree**: the root-down tree produced by a single render call. Pure data, no DOM handles.
- **Component**: a function (or class) whose output is a vtree. Components own local state and re-render on state changes.
- **Reconciler**: the algorithm that walks `(oldTree, newTree)` and decides what to insert/update/remove.
- **Renderer**: the platform-specific backend that turns a patch into actual side effects. `react-dom` is one renderer; `react-native` is another; Ink renders to a terminal. The reconciler is platform-agnostic.
- **Scheduler** (in modern systems): the layer that decides *when* reconciliation work runs and at what priority. React's scheduler uses a lane model; Vue uses a microtask queue.
- **Key**: a stable identity hint on list children that lets the diff match nodes across renders instead of zipping positionally.

### How it relates to the broader landscape

VDOM sits in the family of **declarative UI runtimes**. Its siblings are (a) **fine-grained reactivity** systems like SolidJS, MobX, and Vue's `ref`/`reactive` proxies, where individual signal reads create targeted DOM bindings and no tree is rediffed; and (b) **compile-time DOM** systems like Svelte 4, which translate templates directly into update instructions at build time. A Virtual DOM is the runtime middle path: declarative like signals, general like a compiler, slower than both in the steady state.

## Where

### Where it sits in the rendering pipeline

```
component()  ->  new VTree  ->  diff(old, new)  ->  patch list  ->  real DOM
                                     ^                                  |
                                     |__________ committed VTree <______|
```

It lives entirely on the main thread, in JavaScript, between your component code and `document.*` calls. It runs in the browser, in Node during SSR, and in any host that ships a renderer (React Native for iOS/Android, Ink for terminals, react-three-fiber for WebGL scene graphs).

### Where you typically encounter it

- **React** — the canonical VDOM library; reconciler is Fiber.
- **Vue 2 and Vue 3** — Vue 3's renderer is a VDOM augmented with compiler-emitted patch flags. Vue's upcoming Vapor Mode opts out of VDOM entirely for components that don't need it.
- **Preact** — a 3 kB VDOM with a React-compatible API.
- **Inferno** — a VDOM tuned for raw benchmark speed.
- **Snabbdom** — the lean VDOM library Vue 2 was originally built on; still used as a teaching reference.
- **Mithril, hyperapp** — smaller VDOM frameworks still in production use.

### Ecosystem and tooling

- For **inspecting** vtrees in production apps: React DevTools, Vue DevTools — both let you see the component/fiber tree and inspect props/state.
- For **measuring** reconciliation cost: the browser's Performance panel, React Profiler (records render durations per commit), `why-did-you-render`.
- For **bypassing** the VDOM where it hurts: `useMemo`, `React.memo`, `useTransition`, Vue's `v-once` / `v-memo`, the React Compiler (auto-memoization, stable since React 19).
- For **alternative renderers** of the same VDOM: `react-three-fiber`, `react-native`, `ink`, `react-pdf`.

## When

### When the topic emerged and why

React introduced the Virtual DOM publicly in 2013. The motivating problem at Facebook was the news feed: many small, interdependent state changes producing inconsistent UI when written imperatively (e.g. dropdown that forgot to close, unread badge out of sync). The pre-existing options were jQuery-style direct DOM mutation (fast but un-composable) and dirty-checking from Angular 1 (composable but unpredictable and slow at scale). VDOM gave you `state -> UI` purity without paying for a full DOM rebuild each tick.

Diff-and-patch on a tree mirror is an old idea — XSLT, Erlang's `simple_diff`, and React's own paper trail point back to virtual rendering in game engines. React's contribution was the *heuristic* that made diffing two arbitrary UI trees cheap enough to run on every state change in a real browser.

### When to use it in a project

Reach for it when:
- State updates touch many nodes scattered across the tree (dashboards, editors, chat, social feeds).
- The UI is naturally expressed as `state -> tree` and you don't want to track which DOM nodes need to change by hand.
- You need a single component model that targets multiple backends (web + native + canvas).
- You want server-rendered HTML hydrated by the same code on the client.

### When NOT to use it

Avoid it when:
- The page is mostly static with a sprinkle of interactivity — a `<script>` block or htmx is lighter.
- You need predictably sub-millisecond updates per event (instrument UIs, audio meters, high-frequency canvas overlays) — go imperative or use Solid/Svelte.
- The component count is small but updates are constant (a single counter at 60 fps) — `textContent =` beats any reconciler.
- Your team is small enough that the per-update overhead and the bundle cost (~40 kB for React + ReactDOM gzipped) outweigh the ergonomic win.

## How

### How it works under the hood

The lifecycle of one update in a Fiber-style reconciler:

1. **Trigger.** A state setter (`setState`, `useState`'s setter, a signal write in Vue) marks a component as dirty and schedules work on the reconciler.
2. **Render phase (interruptible).** The reconciler walks from the dirty fiber down, calling each component function to produce new vnodes. It builds a *work-in-progress* tree alongside the *current* tree. This is React's **double-buffering**: each fiber has an `alternate` pointer to its counterpart in the other tree, so React can construct the next version without disturbing what's on screen.
3. **Diffing.** For each pair `(oldVNode, newVNode)` at the same position:
   - If `type` differs (e.g. `div` -> `section`, or `ComponentA` -> `ComponentB`), tear down the old subtree, mount the new one. No further diffing inside.
   - If `type` matches, keep the DOM node, diff props (compute attribute add/remove/update), then recurse into children.
   - For child lists, walk the two arrays in order, matching by `key` where provided. Without keys, the diff is positional — inserting at the head re-patches every following item.
4. **Yielding.** The render phase runs in small units. Between units, the scheduler checks if a higher-priority task (user input, an animation frame) needs the main thread. If so, it pauses, lets the browser breathe, and resumes the work loop later. This is the "concurrent" part of concurrent React.
5. **Commit phase (synchronous, uninterruptible).** Once a full work-in-progress tree is ready, React walks its effect list and applies DOM mutations, runs refs, then runs layout effects and passive effects. The alternate pointers swap: work-in-progress becomes current.
6. **Browser paints.** The next frame reflects the patched DOM.

The diff itself is **O(n)** rather than the textbook tree-edit-distance **O(n³)** because of two assumptions React bakes in:
- Elements of different types yield different trees, so a type change short-circuits the recursion.
- Keys identify stable siblings, turning list reconciliation into a hash-map lookup instead of an alignment problem.

A sketch of keyed list diffing:

```js
function diffChildren(parentDOM, oldChildren, newChildren) {
  const oldByKey = new Map(oldChildren.map(c => [c.key, c]));
  let lastIndex = 0;
  newChildren.forEach((newChild, i) => {
    const oldChild = oldByKey.get(newChild.key);
    if (oldChild) {
      patch(oldChild, newChild);                 // same key -> update in place
      if (oldChild.index < lastIndex) move(parentDOM, oldChild.dom, i);
      else lastIndex = oldChild.index;
      oldByKey.delete(newChild.key);
    } else {
      mount(parentDOM, newChild, i);             // new key -> insert
    }
  });
  for (const leftover of oldByKey.values()) unmount(parentDOM, leftover);
}
```

Vue 3 adds a compile-time twist: its template compiler emits **patch flags** on vnodes so the runtime knows, for example, "this element's only dynamic part is its `class`," letting the diff skip everything else. It also gathers dynamic descendants into a flat **block** array, so an update walks only dynamic nodes — `O(dynamic)`, not `O(total)`.

### Key trade-offs

| Design choice | Gain | Give up |
|---|---|---|
| Diff a tree mirror instead of touching the DOM | Declarative code; cross-platform renderers; predictable `state -> UI` | Per-update CPU + allocation cost; bundle size for the reconciler |
| O(n) heuristic diff (same-type, keys) | Cheap enough to run every render | Cross-type moves and reordering without keys are pessimal |
| Interruptible render phase (Fiber) | Long renders no longer freeze input | Effects can run more than once across pauses; more complex mental model |
| Component-level memoization (`React.memo`, `useMemo`) | Skip subtrees when inputs are equal | Manual referential-equality bookkeeping; easy to misuse |
| Compiler-assisted VDOM (Vue 3 patch flags, React Compiler) | Reduce work without changing user-facing API | Tighter coupling between compiler and runtime |
| VDOM over signals | One mental model, no subscription graph to wire | Steady-state updates traverse more code than a signal write |

### Common failure modes

- **Keys based on array index in a reorderable list** — the diff matches positions, not items, so state attached to children (input focus, animations) jumps to the wrong row.
- **A new object literal as a prop on every render** — breaks `React.memo`/`PureComponent` shallow equality, defeats memoization, re-renders the whole subtree.
- **Wrapping the entire app in a context whose value is a fresh object** — every consumer reconciles on every render.
- **Type oscillation** — conditionally swapping `<div>` for `<section>` around the same children forces unmount/remount, losing DOM state.
- **Giant flat list without virtualization** — the diff is O(n) but n = 50,000; combine with `react-window` or `vue-virtual-scroller`.
- **Effect cascades** — a `useEffect` writing state that triggers another render, which triggers another effect; an infinite reconciliation loop until React bails out with a warning.

## Why

### Why it exists

The fundamental tension is that the DOM is an imperative, stateful, slow-to-mutate API, but UI logic is naturally expressed as a pure function of state. Bridging that gap by hand — tracking which nodes need which attribute changes — does not compose across a team of engineers. The VDOM exists to make the bridge automatic at the cost of some bookkeeping CPU. It also batches mutations: instead of writing to the DOM mid-computation (which can trigger forced layout reads), it collects everything and writes in one commit pass.

### Why it looks the way it does

An obvious alternative is **fine-grained reactivity**: at component setup time, every state-dependent expression registers itself as a subscriber and updates exactly the DOM nodes that read it. This is what Knockout did in 2010 and what SolidJS, MobX, and Vue's reactive system do today. It is faster in the steady state — no diff, no tree walk — but it requires either explicit signal primitives (Solid, Svelte 5 runes) or proxies over your state (Vue, MobX), and the subscription graph itself has memory and setup costs. React's bet was the opposite: keep the user-facing model "just call a function with new props" and pay for the diff. That bet is what makes React components plain functions and what makes a `useState` setter feel free at the call site.

The other alternative is **compile-time DOM**, exemplified by Svelte 4: the compiler emits per-component update functions that mutate exactly the right nodes. No runtime diff, tiny bundle. The trade-off is that the compiler must see the template statically — dynamic component types, higher-order components, and runtime tree shapes are harder. The VDOM gives that flexibility back at runtime.

So: VDOM is the design you pick when you want declarative code, runtime flexibility, and one mental model, and you're willing to spend CPU on a diff to get them.

### Why it matters now

The VDOM is neither growing nor going away in 2026. The narrative has shifted: pure VDOM is losing benchmark battles to Solid and Svelte 5, but the response from VDOM frameworks has not been to abandon the model — it has been to layer compilers and signals *on top*. The React Compiler (stable in React 19) auto-memoizes components so most `useMemo`/`useCallback` calls are unnecessary. Vue 3.6 introduced Vapor Mode, a per-component opt-out of the VDOM that compiles to direct DOM updates. Both directions accept the same conclusion: the VDOM is the right *default*, but the steady-state cost is worth optimizing away when the compiler can prove it's safe.

For a working engineer the practical implication is: you will be reading and writing VDOM-based code for years, but the levers to make it fast (memoization, keys, splitting state, compiler hints) are no longer optional knowledge — they are the topic itself.

## Open questions / things to verify in practice

- How much real-world variance does the React Compiler eliminate vs. hand-written `useMemo`? Benchmark a moderately complex screen with and without it.
- Profile a keyed vs. index-keyed list of 1000 rows where a single row is inserted at index 0. How many DOM operations does each emit?
- Trigger a long synchronous render (e.g. 5000 nodes) and confirm with the Performance panel that React 18+ actually yields to user input mid-render.
- Compare the rendered output of the same component in React, Preact, and Vue 3 under identical state churn; the VDOM model is the same but the constant factors differ noticeably.
- In Vue 3, inspect a compiled template (`vue-template-explorer`) and identify which vnodes carry patch flags vs. which were hoisted as static.
- Find a place in your own codebase where a memoized component still re-renders, and trace which prop's referential identity changed. The answer is almost always more interesting than expected.
