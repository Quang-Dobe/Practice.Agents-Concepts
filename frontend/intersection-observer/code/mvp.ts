/**
 * IntersectionObserver MVP — a single shared observer watching ~10 cards
 * plus one lazy-loaded panel. Demonstrates four load-bearing ideas from
 * the deep-dive doc:
 *
 *   1. ONE observer for many targets (Best Practice #1). Every card and the
 *      lazy panel use the same `observer` instance.
 *   2. A threshold ARRAY, not a single value, so we can distinguish partial
 *      from fully visible (Best Practice #4).
 *   3. `unobserve(target)` inside the callback for a one-shot lazy-load
 *      (Best Practice #2). After the panel first intersects, it stops
 *      costing anything.
 *   4. Branching on `isIntersecting`, NOT `intersectionRatio > 0` — the
 *      spec says the boolean is true even at ratio 0 on the exact
 *      crossing frame.
 *
 * Run this file as-is by compiling with `tsc mvp.ts` and serving the
 * resulting `mvp.js` from an HTML page — or just open `mvp.html`, which
 * inlines the identical JavaScript.
 */

type Visibility = 'OFF SCREEN' | 'PARTIALLY VISIBLE' | 'FULLY VISIBLE';

// Explicit null-checks over `!` — the conventions ban non-null assertions
// in demo code because the demo should show what "handled" looks like.
const logEl = document.querySelector<HTMLPreElement>('#log');
const feedEl = document.querySelector<HTMLDivElement>('#feed');
const lazyEl = document.querySelector<HTMLDivElement>('#lazy-panel');
if (!logEl || !feedEl || !lazyEl) {
  throw new Error('mvp: required DOM roots missing');
}

// Ring buffer of the last 5 entry summaries — rendered into the sticky log.
const recent: string[] = [];
// Off->on transition counter per target. Map keyed by Element is the
// idiomatic way to attach state to a DOM node without touching dataset.
const crossings = new Map<Element, number>();
// Remember the previous `isIntersecting` so we can detect the OFF -> ON
// edge (going PARTIAL -> FULL should not bump the counter).
const wasIntersecting = new Map<Element, boolean>();

const labelFor = (ratio: number, isIntersecting: boolean): Visibility => {
  // Branch on the boolean first — see load-bearing idea (4) above.
  if (!isIntersecting) return 'OFF SCREEN';
  // Ratio 1.0 is unreachable if the target is taller than the root; our
  // cards are short enough that they do hit it, so this comparison is safe.
  if (ratio >= 0.999) return 'FULLY VISIBLE';
  return 'PARTIALLY VISIBLE';
};

// Named `function` declaration — the ONE case the conventions allow — so
// the constructor below can reference it before it appears in source.
function handleEntries(entries: IntersectionObserverEntry[]): void {
  for (const entry of entries) {
    // (a) Push a summary line into the ring buffer; keep only the newest 5.
    recent.unshift(
      `${entry.target.id}  ratio=${entry.intersectionRatio.toFixed(2)}  in=${entry.isIntersecting}`,
    );
    if (recent.length > 5) recent.length = 5;

    // (b) Bump "seen" count only on the OFF -> ON transition. Threshold
    // arrays fire on every band crossing; without this guard, scrolling a
    // card slowly through 0 -> 0.5 -> 1 would triple-count it.
    const wasIn = wasIntersecting.get(entry.target) ?? false;
    if (entry.isIntersecting && !wasIn) {
      crossings.set(entry.target, (crossings.get(entry.target) ?? 0) + 1);
    }
    wasIntersecting.set(entry.target, entry.isIntersecting);

    // (c) Update the card's inline label + counter. Query inside the card
    // rather than caching — negligible cost, keeps state out of module scope.
    const stateEl = entry.target.querySelector<HTMLSpanElement>('.state');
    const countEl = entry.target.querySelector<HTMLSpanElement>('.count');
    if (stateEl) stateEl.textContent = labelFor(entry.intersectionRatio, entry.isIntersecting);
    if (countEl) countEl.textContent = String(crossings.get(entry.target) ?? 0);

    // (d) One-shot: reveal the payload and stop observing this target.
    // After this line runs once, the observer's per-frame work no longer
    // includes the lazy panel — this is the exact pattern lazy-image
    // libraries use.
    if (entry.target === lazyEl && entry.isIntersecting) {
      revealLazyPanel(lazyEl);
      observer.unobserve(lazyEl);
    }
  }
  logEl.textContent = recent.join('\n');
}

const revealLazyPanel = (el: HTMLDivElement): void => {
  el.classList.add('loaded');
  el.textContent = `LAZY PAYLOAD loaded at ${new Date().toLocaleTimeString()}`;
};

// The ONE shared observer. `root: null` means the top-level viewport
// (NOT the nearest scrollable ancestor — a subtle spec quirk). The
// three-value threshold gives us the three visibility bands the label
// function reads.
const observer = new IntersectionObserver(handleEntries, {
  root: null,
  rootMargin: '0px',
  threshold: [0, 0.5, 1],
});

// Build 10 cards and observe each one with the same observer instance.
for (let i = 0; i < 10; i += 1) {
  const card = document.createElement('div');
  card.className = 'card';
  card.id = `card-${i}`;
  card.innerHTML =
    `<b>Card ${i}</b> &middot; <span class="state">OFF SCREEN</span> &middot; ` +
    `seen <span class="count">0</span> time(s)`;
  feedEl.appendChild(card);
  observer.observe(card);
}

// Observe the lazy panel with the SAME instance — proving the "share one
// observer" and "unobserve on one-shot" patterns coexist cleanly.
observer.observe(lazyEl);
