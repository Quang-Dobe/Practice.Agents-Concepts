// The smallest honest demo of a Web Component: <counter-button> — a real
// HTML element the browser understands, with its own shadow tree, styles
// that cannot escape or be reached from outside, an attribute reflected as
// a JS property, and a typed CustomEvent it dispatches on click.

export type CounterChangeDetail = {
  count: number;
  delta: 1 | -1;
};

// Constructable stylesheet shared by every instance. If we instead put a
// <style> tag inside the shadow root, each new <counter-button> would re-parse
// the same CSS — a real cost on component-heavy pages.
const sheet = new CSSStyleSheet();
sheet.replaceSync(`
  :host { display: inline-flex; align-items: center; gap: 0.5rem;
          font-family: system-ui, sans-serif; }
  :host([disabled]) { opacity: 0.4; pointer-events: none; }
  button { padding: 0.25rem 0.75rem; border-radius: 0.25rem;
           border: 1px solid #52525b; background: #f4f4f5; cursor: pointer;
           font: inherit; color: inherit; }
  button:hover { background: #e4e4e7; }
  output { min-width: 2ch; text-align: center; font-variant-numeric: tabular-nums; }
`);

export class CounterButton extends HTMLElement {
  // The browser only calls attributeChangedCallback for attributes named here.
  // Anything else is skipped as a performance optimisation — a bloated list
  // turns every unrelated attribute mutation into a callback.
  static readonly observedAttributes = ['count'] as const;

  readonly #root: ShadowRoot;
  readonly #output: HTMLOutputElement;
  #abort: AbortController | undefined;

  constructor() {
    super();
    // 'open' so tests and DevTools can still reach shadowRoot. 'closed' hides
    // it from external JS but also blocks your own tooling — it is not a
    // security boundary.
    this.#root = this.attachShadow({ mode: 'open' });
    this.#root.adoptedStyleSheets = [sheet];
    this.#root.innerHTML = `
      <button type="button" part="button">
        <slot>Click me</slot>
      </button>
      <output part="value">0</output>
    `;
    const output = this.#root.querySelector('output');
    if (!output) throw new Error('shadow template missing <output>');
    this.#output = output;
  }

  // Property/attribute reflection: the outside world can drive state via
  // markup (<counter-button count="10">), CSS ([count="10"]), or JS
  // (el.count = 10) and all three stay in sync.
  get count(): number {
    return Number(this.getAttribute('count') ?? 0);
  }

  set count(value: number) {
    this.setAttribute('count', String(value));
  }

  connectedCallback(): void {
    // DOM/listener wiring belongs here, not in the constructor. The spec
    // forbids child mutation before insertion, and connectedCallback fires
    // again if the element is moved — a fresh AbortController per connect
    // gives us one-line teardown in disconnectedCallback.
    this.#abort = new AbortController();
    const button = this.#root.querySelector('button');
    button?.addEventListener('click', this.#handleClick, { signal: this.#abort.signal });
    this.#render();
  }

  disconnectedCallback(): void {
    this.#abort?.abort();
  }

  attributeChangedCallback(name: string, _old: string | null, _next: string | null): void {
    if (name === 'count') this.#render();
  }

  #handleClick = (): void => {
    const next = this.count + 1;
    this.count = next;
    // composed: true is what lets the event escape the shadow boundary at
    // all. Without it, listeners on document.body would never fire. The
    // 'counter-' prefix avoids collisions with future native event names.
    this.dispatchEvent(new CustomEvent<CounterChangeDetail>('counter-change', {
      detail: { count: next, delta: 1 },
      bubbles: true,
      composed: true,
    }));
  };

  #render(): void {
    this.#output.textContent = String(this.count);
  }
}

// Global declaration merging so `document.querySelector('counter-button')`
// returns CounterButton (not Element), and 'counter-change' listeners get a
// typed event.detail without any casts at the call site.
declare global {
  interface HTMLElementTagNameMap {
    'counter-button': CounterButton;
  }
  interface HTMLElementEventMap {
    'counter-change': CustomEvent<CounterChangeDetail>;
  }
}

// Guarded define: a duplicate registration throws and would crash the host
// page. Two copies of this file on one page (bundle split, staged rollout)
// should be a no-op, not a fatal error.
if (!customElements.get('counter-button')) {
  customElements.define('counter-button', CounterButton);
}
