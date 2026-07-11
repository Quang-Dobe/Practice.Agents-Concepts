# Connection Pooling

Connection pooling is the practice of keeping a small stash of already-open database connections around and lending them out to your code, instead of opening a brand new one every time the application needs to talk to the database. The pool sits inside your app (or in a separate process like PgBouncer or RDS Proxy) and hides all of the borrow-and-return bookkeeping behind the driver.

It matters because opening a database connection is surprisingly expensive — a TCP handshake, a TLS handshake, authentication, and session setup can add up to 20–100 milliseconds before you send a single query. In a web service under real traffic, that cost quickly dominates the request budget and makes the database server work far harder than it needs to. Engineers reach for a pool anytime a service, worker, or API is going to fire many queries against the same database. They skip it only for one-shot scripts or extremely low-traffic tools where a single connection is enough.

Think of a busy hotel that used to send one concierge up and down from the top floor for every guest. The fix is to station ten concierges permanently in the lobby and have guests take a numbered ticket, grabbing whichever one is free. If all ten are busy, guests wait briefly in line. That lobby is the pool, the concierges are the open connections, and your application code is the guest — the driver quietly hands out and collects tickets on its behalf.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/backend/connection-pooling/
