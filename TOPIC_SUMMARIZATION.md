# IntersectionObserver

IntersectionObserver is a browser Web API that tells you when an element enters or leaves a visible area, usually the viewport. You list the elements you care about, and the browser calls you back only when their visibility crosses a line you picked, so you never have to measure positions yourself.

It matters because many features depend on whether something is on screen: lazy-loading images, infinite scroll, "mark as read", ad impressions, and scroll-triggered animations. The old way listened to scroll events and called getBoundingClientRect() on each element dozens of times per second, which forced layout work on the main thread. The browser already computes layout while rendering, so letting it do the geometry is cheaper. Two settings shape the rule: a threshold (how much of the element must show, from 0 to 1) and a root margin (grow or shrink the visible box, so a lazy-loader can start fetching an image 200px before it appears). It is not the right tool for pixel-accurate per-frame effects like parallax, because callbacks arrive batched and asynchronously.

Picture a security guard watching CCTV monitors. Instead of phoning every 16 ms to ask whether Alice is on camera, you hand the guard a list and a rule: "radio me when Alice is at least half on screen, and again when she leaves." The guard reports only when something crosses that line, and bundles several reports together.

---

Full notes: https://quang-dobe.github.io/Practice.Agents-Concepts/frontend/intersectionobserver/present/index.html
