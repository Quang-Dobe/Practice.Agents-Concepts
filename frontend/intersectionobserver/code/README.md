# IntersectionObserver — MVP Code

The smallest runnable demo of IntersectionObserver: a lazy-loading infinite feed. About 52 lines of actual code, comments excluded.

## What it demonstrates
- **Initial entry on `observe()`**: every card gets one entry right away. Off-screen cards report `isIntersecting=false`, so the callback branches on the flag (deep dive § Registration).
- **Batched async delivery**: one callback per rendering step with all entries, logged with `performance.now() - entry.time` (deep dive § How, step 7).
- **One shared observer + `unobserve`** for one-shot lazy loads (practice #3, #5).
- **Sentinel stall and re-arm**: a sentinel that stays visible changes no state, so it fires no new entry. `unobserve` + `observe` forces a fresh one (deep dive § Common failure modes).

## Prerequisites
- Node 20+ and a Chromium, Firefox, or Safari browser
- `npm install` (dev deps: `typescript`, `vite`)

## Run it

```bash
cd frontend/intersectionobserver/code
npm install
npm run dev        # open the printed http://localhost:5173
```

## Expected output
The right-hand panel shows newest lines on top (also in the DevTools console). Read it from the bottom up.

```
page 1 appended
card batch: 3 entries, delivered 0.3ms after compute
  card 1.1 isIntersecting=true ratio=1.00
page 2 appended                       <- sentinel still visible, re-arm fired it again
page 3 appended                       <- after you scroll down
card batch: 3 entries, delivered 0.1ms after compute
  card 3.2 isIntersecting=false ratio=0.00   <- initial entry, off screen
```

## What to try next
- Set `REARM_SENTINEL = false`: only page 1 loads. Scrolling down fires nothing more, which is the stall.
- Delete `if (!entry.isIntersecting) continue;` and every off-screen card turns green immediately.
- Change `rootMargin` to `'0px 0px 600px 0px'` and watch cards load well before they come into view.
- Remove `observer.unobserve(entry.target)` and scroll up and down. Cards keep logging leave/enter entries.
