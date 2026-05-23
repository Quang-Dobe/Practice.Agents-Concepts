# Virtual DOM — MVP Code

A hand-rolled Virtual DOM in ~125 lines of TypeScript. No React, no Vue, no Preact — just `h`, `mount`, and `patch` so the diff/reconcile mechanism is fully visible.

## What this shows

The same pipeline the deep dive describes: a render function returns a fresh tree of plain `{ tag, props, children, key }` objects, `patch(oldTree, newTree)` walks both trees in parallel, and only the *minimum* set of `createElement` / `setAttribute` / `replaceChild` calls actually fires. Every real DOM mutation is logged so you can watch the heuristics — "same tag stays, different tag swaps", positional vs. keyed child diffing — turn into real browser work.

## Prerequisites

- Node 20+
- One dev dependency: Vite (used only to serve TypeScript to the browser; the VDOM itself has zero runtime dependencies).

## Run it

```bash
cd frontend/virtual-dom/code
npm install
npm run dev
```

Open the printed `http://localhost:5173` URL, then DevTools (F12) → Console. Each interaction prints a grouped `render (count=N, todos=M)` block listing only the DOM mutations the diff produced.

## What to watch for

- Click **+1** a few times. The console shows a single `replace text "Count: 0" -> "Count: 1"` — the `<h2>`, the buttons, and the entire todo list are *not* touched.
- Click **add todo at top** twice. With keyed list diffing, existing `<li>` nodes are reused (you'll see `insert keyed child 2`, then `insert keyed child 3`) — old items log nothing because their subtree is unchanged.
- Delete the first todo. Only `remove keyed child 1` fires; the remaining `<li>` nodes keep their DOM identity. Open the **Elements** panel and watch the same `<li>` survive across renders.
- Try changing the `key` on the `<li>` to `key: Math.random()` in `mvp.ts` — every list change now logs a full remount. That is the index-key bug the practice doc warns about.

## What to try next

- Remove `key: t.id` from the `<li>` to fall back to the positional branch; compare the log volume on "add at top".
- Change `<h3>` to `<section>` between renders and watch the `replace element` log fire — the whole subtree is rebuilt.
- Add a `style` prop on a `<li>` (e.g. `style: 'color: red'`) and confirm only `set attr style` fires on the changed row.
- Wrap the `count` text in its own `<span>` and observe that the `<button>` siblings still emit no mutations.
