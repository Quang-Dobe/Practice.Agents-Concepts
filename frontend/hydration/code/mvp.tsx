/**
 * Hydration MVP — the smallest end-to-end SSR -> hydrate handshake.
 *
 * What this file proves:
 *   1. The server renders <App initial={N}/> to an HTML *string* via
 *      `renderToString`. No JS has run in the browser yet. The user sees the
 *      count and the button as plain text.
 *   2. The browser receives a host page whose <div id="root"> ALREADY contains
 *      that markup, plus a <script type="module"> pointing at /client.js.
 *   3. The client bundle (built once at startup by esbuild) calls
 *      `hydrateRoot(container, <App initial={N}/>)`. React walks the existing
 *      DOM and the new Fiber tree in lockstep, adopts the nodes that are
 *      already there, and attaches the onClick listener.
 *   4. Clicking the button now updates state — the page is live.
 *
 * Run: `npm install && npm start`, then open http://localhost:3000.
 * Watch the terminal: the SSR HTML is logged before any JS reaches the browser.
 */

import { createServer } from 'node:http';
import { fileURLToPath } from 'node:url';
import { renderToString } from 'react-dom/server';
import * as esbuild from 'esbuild';

import { App } from './app.tsx';

const INITIAL = 7;

// Bundle the client entry once at startup. esbuild compiles client.tsx + React
// into one ES module string we serve at /client.js. `platform: 'browser'` is
// what makes esbuild treat React's environment as browser-shaped, not Node.
const clientEntry = fileURLToPath(new URL('./client.tsx', import.meta.url));
const bundle = await esbuild.build({
  entryPoints: [clientEntry],
  bundle: true,
  format: 'esm',
  platform: 'browser',
  write: false,
  define: { 'process.env.NODE_ENV': '"production"' },
  // Inject the same `initial` value the server rendered with so the client's
  // first render matches the server HTML byte-for-byte. This is the
  // load-bearing line for *avoiding* a hydration mismatch.
  banner: { js: `globalThis.__INITIAL__ = ${INITIAL};` },
});
const clientJs = bundle.outputFiles[0]?.text ?? '';

const server = createServer((req, res) => {
  if (req.url === '/client.js') {
    res.writeHead(200, { 'content-type': 'text/javascript' });
    res.end(clientJs);
    return;
  }

  // Step 1: render the React tree to an HTML string. The output contains the
  // text and structure but NO event handlers — plain HTML can't carry
  // `onClick`. That is exactly why hydration has to come along later and
  // re-attach behaviour to the same DOM nodes.
  const appHtml = renderToString(<App initial={INITIAL} />);

  console.log('--- SSR HTML sent to browser ---');
  console.log(appHtml);
  console.log('--- end SSR HTML ---\n');

  // Step 2: ship a page whose <div id="root"> ALREADY contains the rendered
  // markup. The browser paints it immediately (FCP). Then the
  // <script type="module"> loads, and the client's `hydrateRoot` call adopts
  // the very same DOM nodes.
  res.writeHead(200, { 'content-type': 'text/html' });
  res.end(`<!doctype html>
<html>
  <head><meta charset='utf-8'><title>Hydration MVP</title></head>
  <body>
    <div id='root'>${appHtml}</div>
    <script type='module' src='/client.js'></script>
  </body>
</html>`);
});

server.listen(3000, () => {
  console.log('Open http://localhost:3000 — view source to see SSR HTML before JS runs.');
});
