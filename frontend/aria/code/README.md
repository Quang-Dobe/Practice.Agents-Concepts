# ARIA (WAI-ARIA) — MVP Code

The smallest runnable demo of ARIA: the APG Tabs pattern, built by hand. About 70 lines of actual code, comments excluded.

## What it demonstrates
- Roles, states, and relations (`role="tab"`, `aria-selected`, `aria-controls`, `aria-labelledby`) only change what the accessibility tree reports (`02-deep-dive.md § What`).
- The keyboard contract the role promises (Left/Right, Home/End, roving `tabindex`) is your code, not ARIA's (`02-deep-dive.md § How`, step 7).
- One `selected` variable drives every ARIA state, and the CSS styles from `[aria-selected]`, so semantics and visuals cannot drift (`03-practice.md` #4).
- Native `<button>` tabs get focus and Enter/Space for free. Per-instance IDs keep IDREFs valid (`03-practice.md` #1, #9).

## Prerequisites
- Node 20+
- `npm install` (installs `typescript` and `vite`)

## Run it

```bash
npm install
npm run dev     # open the printed URL
npm run build   # optional: strict tsc type-check + production build
```

## Expected output
In the browser, press Tab to focus "Profile", then press the arrow keys. The selected tab moves and its panel appears. The DevTools console logs what a screen reader would announce:

```
[a11y] "Billing", tab, selected, 2 of 3
[a11y] "Security", tab, selected, 3 of 3
[a11y] "Profile", tab, selected, 1 of 3
```

DevTools > Elements > Accessibility shows `tab "Billing"` with `selected: true`.

## What to try next
- Set `ACTIVATION` to `'manual'`. Arrows now only move focus, and Enter or Space selects the tab.
- Change `createElement('button')` to `'div'` in manual mode. Enter no longer activates anything, but the role still says "tab".
- Delete the `aria-selected` line in `render()`. The underline disappears too, because the CSS reads the ARIA state.
- Call `mountTabs` twice. The IDs stay unique, so each panel is still named by its own tab.
