// A hand-rolled Virtual DOM. Three load-bearing pieces of every VDOM library:
//   1. `VNode` — a plain JS object describing one element. No DOM references.
//   2. `mount()` — turns a vnode tree into real DOM the first time.
//   3. `patch()` — diffs (oldVNode, newVNode) and emits the *minimum*
//      DOM mutations needed to reconcile the real DOM with the new tree.
//
// Every real DOM mutation is wrapped in `log()` so the browser console
// shows the reconciler doing as little work as possible.

// ---------------------------------------------------------------------------
// 1. VNode shape + hyperscript factory
// ---------------------------------------------------------------------------

// Text is just a string; elements are tagged objects. JSX would compile to
// `h(...)` calls — here we write them by hand to keep the surface small.
type VNode = string | ElementVNode;
type ElementVNode = { tag: string; props: Props; children: VNode[]; key?: string | number };
type Props = Record<string, unknown>;

const h = (tag: string, props: Props | null, children: VNode[] = []): ElementVNode => {
  const node: ElementVNode = { tag, props: props ?? {}, children };
  // Only set `key` when one was passed — exactOptionalPropertyTypes won't accept undefined.
  const k = props?.key;
  if (typeof k === 'string' || typeof k === 'number') node.key = k;
  return node;
};

const log = (...args: unknown[]): void => console.log('[vdom]', ...args);

// Each element-vnode keeps a back-pointer to the real DOM node it produced.
// Real frameworks store this on the fiber/vnode itself; a WeakMap keeps the
// type literal clean for the reader.
const domOf = new WeakMap<ElementVNode, Element>();

// ---------------------------------------------------------------------------
// 2. mount — initial render. No diffing, just create + append.
// ---------------------------------------------------------------------------

const createNode = (vnode: VNode): Node => {
  if (typeof vnode === 'string') {
    log('create text', JSON.stringify(vnode));
    return document.createTextNode(vnode);
  }
  log('create element', `<${vnode.tag}>`);
  const el = document.createElement(vnode.tag);
  applyProps(el, {}, vnode.props);
  for (const child of vnode.children) el.appendChild(createNode(child));
  domOf.set(vnode, el);
  return el;
};

const mount = (vnode: VNode, container: Element): void => {
  container.appendChild(createNode(vnode));
};

// ---------------------------------------------------------------------------
// 3. Props diff — add new, remove gone, update changed. `on*` keys become
// real event listeners; everything else maps to setAttribute.
// ---------------------------------------------------------------------------

const applyProps = (el: Element, oldProps: Props, newProps: Props): void => {
  const all = new Set([...Object.keys(oldProps), ...Object.keys(newProps)]);
  for (const key of all) {
    if (key === 'key') continue;
    const oldVal = oldProps[key], newVal = newProps[key];
    if (oldVal === newVal) continue;
    if (key.startsWith('on')) {
      const evt = key.slice(2).toLowerCase();
      if (oldVal) el.removeEventListener(evt, oldVal as EventListener);
      if (newVal) el.addEventListener(evt, newVal as EventListener);
    } else if (newVal === undefined) { log('remove attr', key); el.removeAttribute(key); }
    else { log('set attr', key, '=', newVal); el.setAttribute(key, String(newVal)); }
  }
};

// ---------------------------------------------------------------------------
// 4. patch — the diff. The whole point of the VDOM.
// ---------------------------------------------------------------------------

const patch = (oldVNode: VNode, newVNode: VNode, parent: Element, index: number): void => {
  // Case A: any side is text — only touch nodeValue when the string changed.
  if (typeof oldVNode === 'string' || typeof newVNode === 'string') {
    if (oldVNode === newVNode) return;
    log('replace text', JSON.stringify(oldVNode), '->', JSON.stringify(newVNode));
    const oldNode = typeof oldVNode === 'string' ? parent.childNodes[index]! : domOf.get(oldVNode)!;
    parent.replaceChild(createNode(newVNode), oldNode);
    return;
  }
  // Case B: different tag — tear down, mount fresh. React's "same type stays,
  // different type swaps" heuristic; no deep diff inside.
  if (oldVNode.tag !== newVNode.tag) {
    log('replace element', `<${oldVNode.tag}>`, '->', `<${newVNode.tag}>`);
    parent.replaceChild(createNode(newVNode), domOf.get(oldVNode)!);
    return;
  }
  // Case C: same tag — reuse the DOM node, diff props, recurse into children.
  const el = domOf.get(oldVNode)!;
  domOf.set(newVNode, el);
  applyProps(el, oldVNode.props, newVNode.props);
  patchChildren(el, oldVNode.children, newVNode.children);
};

// Child diff. If every child on both sides has a key we run the keyed branch
// (Map lookup by identity); otherwise we pair positionally. Index keys would
// silently work until rows reorder — see the practice doc.
const patchChildren = (parent: Element, oldKids: VNode[], newKids: VNode[]): void => {
  const isKeyed = (v: VNode[]): boolean => v.length > 0 && v.every(c => typeof c !== 'string' && c.key !== undefined);

  if (!isKeyed(oldKids) || !isKeyed(newKids)) {
    const common = Math.min(oldKids.length, newKids.length);
    for (let i = 0; i < common; i++) patch(oldKids[i]!, newKids[i]!, parent, i);
    for (let i = common; i < newKids.length; i++) {
      log('insert child'); parent.appendChild(createNode(newKids[i]!));
    }
    for (let i = oldKids.length - 1; i >= common; i--) {
      log('remove child');
      const stale = oldKids[i]!;
      parent.removeChild(typeof stale === 'string' ? parent.childNodes[i]! : domOf.get(stale)!);
    }
    return;
  }
  // Keyed diff: match by key, mount unmatched, remove leftovers, then a single
  // appendChild pass per child to reorder. appendChild on an existing node is
  // a move, not a clone — idempotent when order already matches.
  const oldByKey = new Map<string | number, ElementVNode>();
  for (const c of oldKids as ElementVNode[]) oldByKey.set(c.key!, c);
  for (const newChild of newKids as ElementVNode[]) {
    const old = oldByKey.get(newChild.key!);
    if (old) { patch(old, newChild, parent, 0); oldByKey.delete(newChild.key!); }
    else { log('insert keyed child', newChild.key); parent.appendChild(createNode(newChild)); }
  }
  for (const leftover of oldByKey.values()) {
    log('remove keyed child', leftover.key); parent.removeChild(domOf.get(leftover)!);
  }
  for (const newChild of newKids as ElementVNode[]) parent.appendChild(domOf.get(newChild)!);
};

// ---------------------------------------------------------------------------
// 5. Demo — a counter + a keyed todo list. Every state change builds a fresh
// vtree and hands it to patch(). The console shows what actually fired.
// ---------------------------------------------------------------------------

type Todo = { id: number; text: string };
type State = { count: number; todos: Todo[]; nextId: number };

let state: State = { count: 0, todos: [{ id: 1, text: 'learn vdom' }], nextId: 2 };
let currentTree: VNode | null = null;

const view = (s: State): VNode =>
  h('div', { id: 'app' }, [
    h('h2', null, [`Count: ${s.count}`]),
    h('button', { onClick: () => update({ ...state, count: state.count + 1 }) }, ['+1']),
    h('button', { onClick: () => update({ ...state, count: state.count - 1 }) }, ['-1']),
    h('h3', null, ['Todos (try removing the first one)']),
    h('ul', null, s.todos.map(t =>
      h('li', { key: t.id }, [
        `${t.text} `,
        h('button', { onClick: () => update({ ...state, todos: state.todos.filter(x => x.id !== t.id) }) }, ['x']),
      ]),
    )),
    h('button', {
      onClick: () => update({
        ...state,
        todos: [{ id: state.nextId, text: `task ${state.nextId}` }, ...state.todos],
        nextId: state.nextId + 1,
      }),
    }, ['add todo at top']),
  ]);

const update = (next: State): void => {
  state = next;
  const newTree = view(state);
  const root = document.getElementById('root')!;
  console.group(`render (count=${state.count}, todos=${state.todos.length})`);
  if (currentTree === null) mount(newTree, root);
  else patch(currentTree, newTree, root, 0);
  console.groupEnd();
  currentTree = newTree;
};

update(state);
