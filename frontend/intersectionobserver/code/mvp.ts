// IntersectionObserver MVP: a lazy-loading infinite feed with two observers.
//
// What this file proves (see ../docs/02-deep-dive.md § How):
//   1. Every observe() yields one initial entry, even for off-screen targets
//      (isIntersecting=false). Branch on isIntersecting, never on "callback fired".
//   2. Entries arrive batched, one task after the rendering step that computed them.
//   3. One-shot targets should be unobserve()d once handled.
//   4. A sentinel that stays visible produces no new entry (no state change),
//      so infinite scroll stalls unless you re-arm it.

const feed = document.querySelector<HTMLElement>('#feed');
const sentinel = document.querySelector<HTMLElement>('#sentinel');
const log = document.querySelector<HTMLElement>('#log');
if (!feed || !sentinel || !log) throw new Error('index.html is missing #feed, #sentinel or #log');

// Flip to false to reproduce the "sentinel stays visible" stall from 03-practice.md.
const REARM_SENTINEL = true;
const MAX_PAGES = 8;
let page = 0;

const write = (msg: string): void => {
  console.log(msg);
  log.textContent = `${msg}\n${log.textContent ?? ''}`; // newest on top
};

// ONE shared observer for every card: one config, one batched callback per frame.
const cardObserver = new IntersectionObserver(
  (entries, observer) => {
    // entry.time is when the browser computed geometry; performance.now() is delivery.
    const lagMs = performance.now() - (entries[0]?.time ?? 0);
    write(`card batch: ${entries.length} entries, delivered ${lagMs.toFixed(1)}ms after compute`);

    for (const entry of entries) {
      const id = entry.target.getAttribute('data-id');
      write(`  card ${id} isIntersecting=${entry.isIntersecting} ratio=${entry.intersectionRatio.toFixed(2)}`);

      // The initial entry for off-screen cards lands here with false. Skip it.
      if (!entry.isIntersecting) continue;

      entry.target.textContent = `card ${id}: loaded`; // stand-in for setting img.src
      entry.target.classList.add('loaded');
      observer.unobserve(entry.target); // one-shot: stop paying for geometry on this card
    }
  },
  // Start "loading" 100px before the card reaches the bottom edge of the viewport.
  { rootMargin: '0px 0px 100px 0px', threshold: 0 },
);

const appendPage = (): void => {
  page += 1;
  for (let i = 1; i <= 3; i++) {
    const card = document.createElement('div');
    card.className = 'card';
    card.dataset.id = `${page}.${i}`;
    card.textContent = `card ${page}.${i}: placeholder`;
    feed.append(card);
    cardObserver.observe(card); // queues an initial entry on the next rendering step
  }
  write(`page ${page} appended`);
};

// Separate observer for the sentinel: different job, and it could use a different config.
const sentinelObserver = new IntersectionObserver((entries, observer) => {
  const entry = entries.at(-1); // only the latest state matters
  if (!entry?.isIntersecting) return;

  appendPage();
  if (page >= MAX_PAGES) {
    observer.disconnect();
    write('no more pages: sentinel observer disconnected');
    return;
  }

  // Re-arm. If 3 short cards did not push the sentinel off screen, it is still
  // intersecting, so no state change happens and no callback would ever fire again.
  // unobserve+observe resets previousThresholdIndex to -1, forcing a fresh entry.
  if (!REARM_SENTINEL) return;
  observer.unobserve(sentinel);
  observer.observe(sentinel);
});

sentinelObserver.observe(sentinel);
