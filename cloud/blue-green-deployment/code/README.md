# Blue-Green Deployment — MVP Code

The smallest runnable demo of blue-green deployment. About 90 lines of code, comments excluded.

## What it demonstrates

- The **router pointer** is the deploy — flipping `activeColor` is the entire cutover mechanic (`02-deep-dive.md § How`, step 5).
- A **smoke-test gate** runs against the idle pool before cutover; a failed test aborts the flip and users never see the broken build.
- **Instant rollback** is symmetric — one pointer flip back to the previously-live pool, which was kept warm.
- Continuous synthetic traffic runs through the router so each transition is observable as a change in response text mid-stream.

## Prerequisites

- Node 20+.
- `npm install` in this folder (pulls `tsx` and `typescript` only).

## Run it

```bash
npm install
npm start
```

## Expected output

Timestamped lines, roughly in this shape:

```
[10:00:00.000] === steady state: blue v1.4.0 is live, green is empty ===
[10:00:00.150]   user-req 1 -> OK from blue v1.4.0 (req 1)
[10:00:00.600] === release 1: ship v1.5.0 to green (healthy build) ===
[10:00:00.601] deployed v1.5.0 to idle pool [green] — router still on [blue]
[10:00:01.200] smoke-testing idle pool [green] v1.5.0...
[10:00:01.201]   smoke test PASSED on [green]
[10:00:01.202] CUTOVER: [blue] -> [green] (now serving v1.5.0)
[10:00:01.350]   user-req 9 -> OK from green v1.5.0 (req 9)
...
[10:00:03.500] ROLLBACK requested — flipping [blue] -> [green]
...
[10:00:05.200]   smoke test FAILED on [blue]: pool blue v1.7.0 is broken
[10:00:05.201] CUTOVER ABORTED — staying on [green]
```

Watch the `user-req` lines: they change from `blue v1.4.0` to `green v1.5.0` to `blue v1.6.0` (5xx) and back to `green v1.5.0` without any user request being lost between calls.

## What to try next

- Set `broken: true` in release 1's `deployToIdle` call and watch the smoke test refuse the cutover.
- Lower the `setInterval` from 150ms to 20ms to see how many requests land mid-cutover (none, because the flip is atomic).
- Remove the `smokeTest(idle)` check inside `cutover()` and re-run release 3 to see the consequence of an ungated deploy.
- Add a third color and try to extend the union type — the compiler will tell you everywhere the assumption "exactly two colors" is baked in.
