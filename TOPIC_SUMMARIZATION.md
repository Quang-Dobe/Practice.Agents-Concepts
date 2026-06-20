# CDN Edge Caching

A CDN — a Content Delivery Network — is a fleet of servers spread across the world. Edge caching is the trick that makes that fleet useful: each server keeps a local copy of your website's content, so a user's request gets answered by a machine near them instead of one halfway across the planet. The result is that pages feel fast no matter where the visitor is, and your own backend server stops being a single bottleneck the whole internet has to squeeze through.

You reach for it when you're serving the same bytes to lots of people in lots of places. Static assets — images, video, JavaScript, CSS, fonts — are the obvious win, but cacheable API responses and even whole HTML pages can ride on the same machinery. It is also how teams absorb sudden traffic spikes (a launch, a viral moment, a clumsy DDoS) without scaling the origin one bit. It is the wrong tool when every response is unique to one user or when stale data is genuinely unsafe.

The mental picture is a cookbook author who lives in one city. Without a CDN, every reader flies in to ask for a photocopy. With one, each city gets a library branch: the first Tokyo reader to ask for chapter 3 triggers one trip to the author, the librarian shelves the copy, and every Tokyo reader after that gets it instantly off the local shelf. The author — your origin server — only sees one request per city per chapter instead of millions.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/cloud/cdn-edge-caching/
