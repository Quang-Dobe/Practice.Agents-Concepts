# ARIA — Overview

> WAI-ARIA is a set of HTML attributes (`role`, `aria-*`) that tell assistive technology what a custom UI element *is* and what state it's in, when native HTML can't say it on its own.

## The 30-second version

Screen readers don't look at your pixels. They read a parallel structure the browser builds from your HTML, called the accessibility tree. A real `<button>` shows up there as "button, Save". A `<div>` styled to look like a button shows up as nothing useful. ARIA is the W3C spec that lets you fill in that gap by declaring roles ("this is a tab"), states ("this tab is selected"), and properties ("this tab controls that panel"). If you build any custom widget (tabs, comboboxes, modals, menus), ARIA decides whether a blind user can operate it or only hears silence.

## The mental model

Think of your page as a warehouse of boxes, and the screen reader as a courier who can't see inside them. The courier only reads the **shipping labels**.

Native HTML elements come pre-labeled. A `<button>`, `<input type="checkbox">`, or `<nav>` arrives with a printed label that says what it is and how to handle it. The courier knows what to do.

A `<div>` is a blank box. You can paint it to look like a checkbox, but the courier sees an unlabeled box and moves on.

ARIA is a **label printer**. You can stick `role="checkbox"` and `aria-checked="true"` on that blank box, and now the courier announces "checkbox, checked."

The catch: **the label doesn't change what's inside the box.** ARIA only changes what gets announced, not how the element behaves. Put `role="button"` on a `<div>` and the screen reader says "button", but pressing Enter or Space still does nothing, and Tab still skips over it. You've labeled an empty box "fragile glassware." The courier trusts you, and you lied. That's why the spec's own guidance opens with: if a native HTML element already does the job, use it instead of ARIA.

```html
<!-- Labeled, but still broken: no focus, no keyboard -->
<div role="button" onclick="save()">Save</div>

<!-- Correct: semantics and behavior come built in -->
<button onclick="save()">Save</button>
```

## What it is NOT

- Not a styling or behavior library. It adds no keyboard handling, focus management, or visuals. You write those yourself.
- Not WCAG. WCAG is the set of accessibility *success criteria* you're measured against. ARIA is one technique for meeting some of them.
- Not a replacement for semantic HTML. Native elements are the default. ARIA fills gaps.
- Not visible to most users. Sighted mouse users never experience it, which is exactly why broken ARIA goes unnoticed.

## When you would reach for it

- You're building a widget HTML has no element for: tabs, tree views, comboboxes with autocomplete, grids.
- Content changes without a page load and you need it announced, like a toast or "3 results found" (`aria-live`).
- You need to expose state HTML can't express natively, such as `aria-expanded` on a disclosure toggle.
- Two elements are related but not nested in the DOM, such as an input and the error message that describes it (`aria-describedby`).
- An icon-only button has no visible text (`aria-label="Close"`).

## When you would NOT reach for it

- A native element already exists. Use `<button>`, `<dialog>`, `<details>`, `<select>`, `<nav>`.
- You're adding ARIA "just to be safe." Redundant or wrong ARIA actively misleads users. WebAIM's yearly scans of the top million home pages keep finding *more* detected errors on pages that use ARIA than on pages that don't.
- You aren't going to implement the keyboard behavior the role promises. A half-built `role="menu"` is worse than a plain list of links.

## Key vocabulary (just enough to keep reading)

- **Accessibility tree**: the browser's semantic model of the page that assistive tech reads.
- **Assistive technology (AT)**: screen readers (NVDA, JAWS, VoiceOver), switch devices, voice control.
- **Role**: what an element is (`button`, `tab`, `dialog`, `alert`).
- **State**: a value that changes with interaction (`aria-expanded`, `aria-checked`, `aria-selected`).
- **Property**: a mostly static characteristic (`aria-label`, `aria-controls`, `aria-required`).
- **Accessible name**: the text AT announces for an element, computed from content, `<label>`, or ARIA.
- **Live region**: an area whose updates AT announces automatically (`aria-live="polite"`).
- **APG**: the [ARIA Authoring Practices Guide](https://www.w3.org/WAI/ARIA/apg/), W3C's reference patterns for building common widgets correctly.

## What's next

The deep dive covers What / Where / When / How / Why in detail: how the accessibility tree is built, how accessible names are computed, the five rules of ARIA use, and what a correct tabs widget needs beyond its attributes.
