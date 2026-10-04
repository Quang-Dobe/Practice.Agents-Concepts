// mvp.ts — the APG Tabs pattern, built by hand.
//
// What this file proves: ARIA only *labels* the boxes (role, state, relations).
// Every behaviour the role promises — focus order, arrow keys, Home/End,
// keeping aria-selected true — is code YOU write below. Nothing here is enforced
// by the browser; delete a line and the screen reader is lied to in silence.

type Activation = 'automatic' | 'manual';

// Automatic: arrow keys select immediately. Manual: arrows only move focus,
// Enter/Space selects (use manual when showing a panel is slow or hits the network).
const ACTIVATION: Activation = 'automatic';

const TABS = [
  { label: 'Profile', body: 'Name, avatar, and bio.' },
  { label: 'Billing', body: 'Card ending 4242. Next invoice on 1 Nov.' },
  { label: 'Security', body: 'Two-factor authentication is on.' },
] as const;

let instanceCount = 0;

export const mountTabs = (root: HTMLElement, title: string): void => {
  // Per-instance IDs: a duplicate id silently empties aria-labelledby / aria-controls.
  const uid = `tabs-${++instanceCount}`;
  let selected = 0;

  // Visible heading names the tablist via aria-labelledby (visible text > aria-label).
  const heading = document.createElement('h2');
  heading.id = `${uid}-heading`;
  heading.textContent = title;

  const tablist = document.createElement('div');
  tablist.setAttribute('role', 'tablist');
  tablist.setAttribute('aria-labelledby', heading.id);

  const tabs = TABS.map((t, i) => {
    // A native <button> gives focusability + Enter/Space -> click for free.
    // role="tab" only changes what AT announces ("Billing, tab, 2 of 3").
    const tab = document.createElement('button');
    tab.id = `${uid}-tab-${i}`;
    tab.setAttribute('role', 'tab');
    tab.setAttribute('aria-controls', `${uid}-panel-${i}`); // relation: tab -> its panel
    tab.textContent = t.label; // accessible name computed from content (accname step 2F)
    tab.addEventListener('click', () => select(i));
    return tab;
  });

  const panels = TABS.map((t, i) => {
    const panel = document.createElement('div');
    panel.id = `${uid}-panel-${i}`;
    panel.setAttribute('role', 'tabpanel');
    panel.setAttribute('aria-labelledby', `${uid}-tab-${i}`); // panel is named by its tab
    panel.tabIndex = 0; // APG: panel is reachable by Tab when it holds no focusable content
    panel.textContent = t.body;
    return panel;
  });

  // Single source of truth: ONE variable drives every aria-* state, tabindex, and
  // visibility. The CSS styles from [aria-selected], so visuals cannot drift from semantics.
  const render = (): void => {
    tabs.forEach((tab, i) => {
      const isSelected = i === selected;
      tab.setAttribute('aria-selected', String(isSelected));
      tab.tabIndex = isSelected ? 0 : -1; // roving tabindex: exactly one tab in the Tab order
    });
    // `hidden` removes inactive panels from both the screen and the accessibility tree.
    panels.forEach((panel, i) => { panel.hidden = i !== selected; });
  };

  const select = (i: number): void => {
    selected = i;
    render();
    console.log(`[a11y] "${TABS[i]?.label}", tab, selected, ${i + 1} of ${TABS.length}`);
  };

  // The keyboard contract role="tab" promises (APG). ARIA ships none of it.
  tablist.addEventListener('keydown', (e) => {
    const current = tabs.findIndex((tab) => tab === document.activeElement);
    if (current === -1) return;

    const targets: Record<string, number> = {
      ArrowRight: current + 1,
      ArrowLeft: current - 1,
      Home: 0,
      End: tabs.length - 1,
    };
    const target = targets[e.key];
    if (target === undefined) return; // let Tab, Enter, Space keep their native behaviour

    e.preventDefault(); // stop Home/End from scrolling the page
    const next = (target + tabs.length) % tabs.length; // wrap around at both ends
    tabs[next]?.focus();
    if (ACTIVATION === 'automatic') select(next);
  });

  tablist.append(...tabs);
  root.append(heading, tablist, ...panels);
  render();
};

const app = document.querySelector<HTMLElement>('#app');
if (app === null) throw new Error('index.html is missing <main id="app">');
mountTabs(app, 'Account settings');
