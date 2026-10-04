# ARIA

ARIA, short for WAI-ARIA, is a W3C set of HTML attributes, `role` plus the `aria-*` family, that tells assistive technology such as screen readers what a custom piece of UI is and what state it is in. Screen readers do not look at pixels; they read the accessibility tree the browser builds from your HTML, and ARIA fills the gaps where native HTML cannot describe an element on its own.

It matters whenever you build a widget HTML has no element for, like tabs, tree views, or comboboxes, or when content changes without a page load and needs to be announced, such as a "3 results found" message. It also exposes states like expanded or selected, links elements that are not nested, and names icon-only buttons. The catch is that ARIA only changes what gets announced, not how anything behaves: it adds no keyboard handling or focus management. So the first rule is to use a native element like `<button>` or `<dialog>` when one exists, because wrong or redundant ARIA actively misleads users.

Picture a courier who cannot see inside boxes and only reads shipping labels. A native `<button>` arrives pre-labeled. A styled `<div>` is a blank box. ARIA is a label printer: stick `role="checkbox"` and `aria-checked="true"` on the box and the courier announces "checkbox, checked." But the label does not change the contents. A `<div role="button">` is announced as a button, yet Enter, Space, and Tab still do nothing unless you write that behavior yourself.

---

Full notes: https://quang-dobe.github.io/Practice.Agents-Concepts/frontend/aria/present/index.html
