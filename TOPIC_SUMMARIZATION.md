# Hydration

Hydration is the step where a server-rendered web page, which the browser is already painting as static HTML, gets "woken up" by JavaScript so that buttons, forms, and state-bound elements actually respond to the user. The framework walks the same component tree on the client that the server just rendered, matches it node-for-node against the existing DOM, and attaches event listeners and state instead of throwing the DOM away and rebuilding it.

It matters because modern frontend stacks want two things at once: a fast First Contentful Paint with SEO-friendly markup, and the rich interactivity of a single-page app. Pure client-side rendering ships a blank shell and stalls until JavaScript loads. Pure server rendering paints fast but leaves the page inert. Hydration is the bridge — engineers reach for it whenever they pick a meta-framework like Next.js, Nuxt, SvelteKit, Remix, or SolidStart, and it shapes how they think about bundle size, time-to-interactive, and entire architectural variants like islands and React Server Components.

A useful picture: the server response is an unfurnished house that is already built, with walls and windows in the right places, so a visitor can walk through it the moment they arrive. The JavaScript bundle is the moving truck that pulls up a few seconds later carrying the appliances and light switches. Hydration is the movers walking room by room, matching each appliance to its outlet, and confirming the layout matches the blueprint. If a wall is in a different spot than the blueprint says, they complain loudly — that is a hydration mismatch.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/frontend/hydration/
