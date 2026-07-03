# CSS Grid

CSS Grid is the browser's built-in way to lay out a page as a two-dimensional grid of rows and columns. You mark a parent element as `display: grid`, describe the shape of the grid — how many columns, how wide, how many rows, how tall — and its direct children snap into those cells. Instead of arranging content one axis at a time and hoping the wrapping works out, you design the container's shape first and place items into it.

Engineers reach for CSS Grid whenever a layout needs alignment in both directions at once: overall page scaffolding like header / sidebar / main / footer, dashboard tiles, product-card grids, form fields that line up across sections, or magazine-style layouts where items span uneven blocks. Before Grid, that meant nested divs, floats, absolute positioning, or a three-level-deep Flexbox tree. A native Grid layout collapses all of that into a handful of CSS properties on a single parent, and reshapes cleanly at breakpoints without touching the HTML. Grid does not replace Flexbox — Flexbox is still the right tool for single-axis component-level layout — but for anything two-dimensional it is the correct primitive.

A useful way to picture it is an Excel spreadsheet. You get a matrix of rows and columns before you type anything; cells have coordinates, you can merge them, and content drops into whichever cell you choose. CSS Grid is that spreadsheet for HTML — the row and column structure exists independently of the content, and the layout drives what the content does.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/frontend/css-grid/
