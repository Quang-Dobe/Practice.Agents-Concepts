# CSS Grid

CSS Grid is a browser layout system where the parent element draws the rows and columns of a page first, and each child then claims a rectangle of cells inside that plan. It works in two dimensions at once — rows and columns together — instead of the single direction Flexbox handles. A container declares its tracks with something like `grid-template-columns: 200px 1fr 200px`, and the browser handles the leftover space, the gaps, and the stretching. It has shipped in every major browser since 2017.

An engineer reaches for it whenever things have to line up across both rows and columns: page scaffolding with a header, sidebar, main area and footer; dashboards; pricing tables; card galleries. It also gives responsive layouts without media queries through `repeat(auto-fit, minmax(240px, 1fr))`, and lets elements overlap without `position: absolute`. It does not replace Flexbox — a single row of buttons is less code in Flexbox — and it is not a substitute for a real `<table>` when the data is genuinely tabular.

The useful analogy is an architect's floor plan versus seating people on a bus. Flexbox is the bus: one aisle, one direction, and rows know nothing about each other. Grid is the floor plan: you draw the walls before anyone moves in, then assign tenants — accounting takes the whole top strip, engineering takes columns one through two and rows two through four. Tenants can span several cells, sit on top of each other, or be dropped in unassigned and let the building manager find them a free room.

---

Full notes: https://quang-dobe.github.io/Practice.Agents-Concepts/frontend/css-grid/present/index.html
