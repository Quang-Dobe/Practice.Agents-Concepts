/**
 * Shared component. The whole point of hydration is that the SAME component
 * code runs on both sides:
 *   - On the server, `renderToString` calls it once to produce HTML.
 *   - In the browser, `hydrateRoot` calls it again to build React's Fiber tree
 *     and attach event listeners to the DOM the server already painted.
 *
 * It lives in its own file so the client bundle can import it without pulling
 * in `node:http` / `esbuild` from `mvp.tsx`.
 */

import { useState } from 'react';

export type AppProps = { initial: number };

export const App = ({ initial }: AppProps) => {
  // On the server this hook returns `initial` and never re-renders — the HTML
  // simply contains the number. On the client, `hydrateRoot` re-runs the
  // function with the same `initial`, wires up `setCount`, and *then* the
  // button becomes interactive.
  const [count, setCount] = useState(initial);

  return (
    <main>
      <h1>Hydration demo</h1>
      <p>
        Count: <span data-testid='count'>{count}</span>
      </p>
      <button onClick={() => setCount((c) => c + 1)}>increment</button>
      <p style={{ color: '#666' }}>
        Before the client bundle loads, this button is dead text. After
        hydration, it works.
      </p>
    </main>
  );
};
