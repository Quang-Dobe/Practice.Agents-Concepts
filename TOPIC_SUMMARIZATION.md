# IntersectionObserver

IntersectionObserver is a browser API that tells you, asynchronously and efficiently, when an element enters or leaves the visible area of a scroll container — without you having to measure anything yourself. Instead of hooking the scroll event and asking each element "are you on screen yet?" on every frame, you register the elements you care about once, hand the browser a callback, and the browser notifies you only when visibility crosses a threshold you defined.

Engineers reach for it because the old approach — scroll listeners plus getBoundingClientRect on every candidate — is expensive and janks the main thread. IntersectionObserver moves the work off the main thread and batches the callbacks, which makes it both correct and cheap. It is the right tool for lazy-loading images and iframes as they approach the viewport, for implementing infinite scroll by observing a sentinel element at the bottom of a list, for firing analytics impression events only when a card is genuinely seen, and for pausing or autoplaying a video when it scrolls in or out of view.

The mental model is a security guard watching a bank of monitors. You do not want every teller turning around every few milliseconds asking whether anyone has arrived. Instead you post a note to the guard: tell me the moment someone crosses this red line. The guard already watches the door as part of their job, and radios you only when the line is crossed. The red line is the threshold, the room is the root, and the how-far-outside-still-counts is the rootMargin.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/frontend/intersection-observer/
