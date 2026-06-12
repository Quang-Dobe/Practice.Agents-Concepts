# Hydration — MVP Code

The smallest runnable demo of React 18 SSR + `hydrateRoot`. ~90 lines of code across three files.

## What it demonstrates

- **SSR -> hydrate handshake.** `renderToString` produces inert HTML; the browser paints it; `hydrateRoot` adopts the same DOM and wires up events.
- **Shared component.** `app.tsx` is the same source both sides render — the symmetry the React reconciler depends on.
- **Deterministic props bridge.** The server injects `INITIAL` into the client bundle via an esbuild banner so both renders match byte-for-byte.
- **No framework magic.** Plain Node `http`, React 18, and esbuild — the bare mechanism, no Next.js.

## Prerequisites

- Node 20+
- `npm install` (pulls `react`, `react-dom`, `esbuild`, `tsx`)

## Run it

```bash
npm install
npm start
# then open http://localhost:3000
```

The terminal logs the SSR HTML for each request. The browser console logs `hydrated — the button is now interactive` once `/client.js` runs.

## Expected output

Terminal:

```
Open http://localhost:3000 — view source to see SSR HTML before JS runs.
--- SSR HTML sent to browser ---
<main><h1>Hydration demo</h1><p>Count: <span data-testid="count">7</span></p><button>increment</button>...
```

Browser: "Count: 7" plus an `increment` button that works after hydration.

## What to try next

- Edit the banner to inject `= 1` while leaving `INITIAL = 7` — observe React's hydration error in the browser console.
- Swap `hydrateRoot` for `createRoot(container).render(...)` in `client.tsx` — watch the count flash as the client throws away the server DOM.
- Add `{new Date().toISOString()}` to `app.tsx` — the textbook non-deterministic mismatch from `02-deep-dive.md`.
