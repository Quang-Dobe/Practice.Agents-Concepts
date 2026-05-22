# Virtual DOM

The Virtual DOM is a lightweight JavaScript copy of your UI tree that a framework compares against the previous copy so it can update the real browser DOM with the smallest possible set of changes. React popularized the idea, and Vue, Preact, and Inferno all use the same trick: describe what the UI should look like as plain objects, diff the new description against the old one, then patch only the parts that actually changed.

Engineers reach for it because the browser's DOM is expensive to touch and stateful to manage by hand. Writing imperative code that figures out which nodes to create, update, or remove after every state change does not scale. The Virtual DOM lets you write declarative code — a function from state to UI — and lets the framework do the imperative DOM surgery for you. The cost is a small diffing pass on every update; the payoff is a sane programming model and a single batched update to the real DOM.

A useful analogy is a teacher with a whiteboard. Every few seconds a student hands you a fresh photo of what the board should look like. The lazy approach is to erase everything and copy the photo from scratch. The smart approach is to hold the new photo next to the old one, scan for the cells that actually differ, and only rewrite those. The Virtual DOM is the photo — cheap to make, cheap to compare, expensive only at the final patch step where the real DOM is touched.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/frontend/virtual-dom/
