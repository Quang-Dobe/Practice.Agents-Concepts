/**
 * Blue-Green Deployment — minimal demonstration.
 *
 * This file simulates the *cutover mechanic* itself, which is the only thing
 * that is actually interesting about blue-green. Everything around it (real
 * containers, an ALB, a database, drain timers) is removed so the moving
 * parts are visible in one screen.
 *
 * What we model:
 *   - Two homogeneous "pools" (blue, green), each pinned to a version.
 *   - A "router" that holds a single pointer to the currently active color.
 *     Flipping that pointer IS the deployment. That is the whole idea.
 *   - A smoke-test gate that must pass on the idle pool before cutover.
 *   - A continuous stream of synthetic user requests, so the cutover and
 *     rollback are observable in the log instead of being invisible state.
 *
 * What we deliberately do NOT model:
 *   - A real HTTP server. A function call is a clearer "request" here.
 *   - Connection draining. We log it as a one-line note; the real subtlety
 *     (deregistration_delay vs p99) is covered in 02-deep-dive.md.
 *   - A shared database. Both colors would talk to it; out of scope for
 *     a 100-line demo.
 */

// --- domain types --------------------------------------------------------

// Two colors is the whole vocabulary. A string-literal union beats an enum
// because TS narrows it everywhere and there is no runtime emission.
type Color = 'blue' | 'green';

// A pool is one full environment. In production this would be a target
// group, an ECS task set, or a K8s ReplicaSet. Here it is a function and
// a version string — enough to prove the routing concept.
type Pool = {
  color: Color;
  version: string;
  // `broken` simulates "the new build has a latent bug that the smoke test
  // does catch" (gate scenario) or "...does NOT catch" (rollback scenario).
  broken: boolean;
  serve: (requestId: number) => string;
};

// --- the router ----------------------------------------------------------

// The router is literally one mutable pointer. Atomic flip of this field is
// what ALB ModifyListener / K8s Service-selector-patch / Cloud Run traffic
// tag update all reduce to under the hood.
let activeColor: Color = 'blue';

const pools: Record<Color, Pool> = {
  blue: {
    color: 'blue',
    version: 'v1.4.0',
    broken: false,
    serve: (id) => `OK from blue v1.4.0 (req ${id})`,
  },
  green: {
    // Green starts empty — no version deployed yet. This mirrors the
    // steady state before a release: idle target group with zero tasks.
    color: 'green',
    version: '<empty>',
    broken: false,
    serve: () => 'ERR: green has no build',
  },
};

// One log line == one observable moment. Timestamps let the reader see how
// fast the cutover actually is (it is one tick).
const log = (msg: string): void => {
  const ts = new Date().toISOString().slice(11, 23); // HH:MM:SS.mmm
  console.log(`[${ts}] ${msg}`);
};

// Every "user" request hits the router, which forwards to whichever pool
// `activeColor` currently points at. The router has no idea what is
// deployed — that is the entire point of decoupling deploy from release.
const handleRequest = (id: number): string => pools[activeColor].serve(id);

// --- deployment primitives ----------------------------------------------

// Step 1 of a real release: install the new build on the idle color. The
// router is untouched, so zero users see the new version yet.
const deployToIdle = (version: string, broken: boolean): void => {
  const idle: Color = activeColor === 'blue' ? 'green' : 'blue';
  pools[idle] = {
    color: idle,
    version,
    broken,
    // The serve function captures the new version. If broken, it throws —
    // standing in for any production failure mode (5xx, dead DB pool, ...).
    serve: (reqId) => {
      if (broken) throw new Error(`pool ${idle} ${version} is broken`);
      return `OK from ${idle} ${version} (req ${reqId})`;
    },
  };
  log(`deployed ${version} to idle pool [${idle}] — router still on [${activeColor}]`);
};

// The smoke-test gate. In production this is the test listener on a
// separate port, the synthetic transaction, the readiness probe that hits
// the DB. Here it is one direct call — the contract is the same: if this
// throws, we refuse to flip.
const smokeTest = (color: Color): boolean => {
  log(`smoke-testing idle pool [${color}] ${pools[color].version}...`);
  try {
    pools[color].serve(-1);
    log(`  smoke test PASSED on [${color}]`);
    return true;
  } catch (err) {
    log(`  smoke test FAILED on [${color}]: ${(err as Error).message}`);
    return false;
  }
};

// The cutover. One assignment. That is the deploy.
// Gated on smoke test — refusing to flip is the safety property the whole
// pattern exists to provide.
const cutover = (): void => {
  const idle: Color = activeColor === 'blue' ? 'green' : 'blue';
  if (!smokeTest(idle)) {
    log(`CUTOVER ABORTED — staying on [${activeColor}]`);
    return;
  }
  const previous = activeColor;
  activeColor = idle; // <-- the entire mechanic, one line
  log(`CUTOVER: [${previous}] -> [${activeColor}] (now serving ${pools[activeColor].version})`);
  log(`  [${previous}] kept warm; rollback is one pointer flip`);
};

// Rollback is symmetric — flip the pointer the other way. Because the old
// pool was never torn down, this is O(1), not "redeploy the old build".
const rollback = (): void => {
  const previous: Color = activeColor === 'blue' ? 'green' : 'blue';
  log(`ROLLBACK requested — flipping [${activeColor}] -> [${previous}]`);
  activeColor = previous;
  log(`  router now on [${activeColor}] (${pools[activeColor].version})`);
};

// --- a tiny continuous traffic generator --------------------------------

// We fire a request every 150ms in the background. This is what makes the
// cutover visible in the log: you see "OK from blue" lines, then suddenly
// "OK from green" lines, and the moment of transition is the deploy.
let reqCounter = 0;
const userTraffic = setInterval(() => {
  reqCounter += 1;
  try {
    log(`  user-req ${reqCounter} -> ${handleRequest(reqCounter)}`);
  } catch (err) {
    // A broken pool serving real traffic = 5xx in production. We log it
    // so a failed rollback path would be visible if the demo were extended.
    log(`  user-req ${reqCounter} -> 5xx (${(err as Error).message})`);
  }
}, 150);

// --- the deployment narrative -------------------------------------------

// A small sleep helper. Demos read better with pauses than with a wall of
// instant output — the cutover deserves to feel like a discrete event.
const sleep = (ms: number): Promise<void> =>
  new Promise((resolve) => setTimeout(resolve, ms));

const run = async (): Promise<void> => {
  log('=== steady state: blue v1.4.0 is live, green is empty ===');
  await sleep(600);

  // Scenario 1: a healthy release. Smoke test passes, cutover succeeds.
  log('=== release 1: ship v1.5.0 to green (healthy build) ===');
  deployToIdle('v1.5.0', false);
  await sleep(600);
  cutover();
  await sleep(900);

  // Scenario 2: a release that the smoke test would *miss* — say it only
  // breaks on real traffic patterns. Cutover happens, prod 5xx's, on-call
  // hits the rollback button, and we are back on the previous version in
  // one tick. This is the whole reason blue-green exists.
  log('=== release 2: ship v1.6.0 to blue (latent bug, smoke passes) ===');
  deployToIdle('v1.6.0', false);
  await sleep(450);
  cutover(); // flips green -> blue
  // After cutover, flip the broken bit on the now-active pool to simulate
  // "the bug only shows up under real load".
  pools.blue.broken = true;
  pools.blue.serve = () => {
    throw new Error('v1.6.0 dies on real traffic');
  };
  log('  ...real traffic exposes a bug the smoke test did not catch');
  await sleep(900);
  rollback(); // back to green v1.5.0, instantly
  await sleep(900);

  // Scenario 3: the gate doing its job. Build is broken in a way the
  // smoke test does catch — cutover is refused, users never see it.
  log('=== release 3: ship v1.7.0 to blue (broken; smoke test catches it) ===');
  deployToIdle('v1.7.0', true);
  await sleep(450);
  cutover(); // should abort
  await sleep(900);

  clearInterval(userTraffic);
  log('=== done ===');
};

run();
