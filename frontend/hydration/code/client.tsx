/**
 * Client-side hydration entry. The server bundled this file with esbuild and
 * served the result at /client.js. When the browser parses it, this code runs.
 *
 * The single load-bearing call is `hydrateRoot`. It does NOT clear the
 * container and re-render — it walks the existing server-painted DOM and
 * attaches React's Fiber tree to it, installing the delegated event listener
 * for onClick along the way.
 */

import { hydrateRoot } from 'react-dom/client';

import { App } from './app.tsx';

// The server injected this via an esbuild `banner` so the client's first
// render produces the same HTML the server produced. Mismatching this value
// (try editing INITIAL in mvp.tsx to 1 without restarting the page) is the
// simplest way to see React's hydration error in the console.
declare const __INITIAL__: number;

const container = document.getElementById('root');
if (!container) throw new Error('missing #root');

// Compare with `createRoot(container).render(...)`: that would discard the
// server HTML and re-create every DOM node, defeating SSR. `hydrateRoot`
// adopts what's already there.
hydrateRoot(container, <App initial={__INITIAL__} />);

console.log('hydrated — the button is now interactive');
