# Rate Limiting

Rate limiting caps how many requests a single caller can make to a service inside a short window of time. Servers, databases, and downstream APIs all have finite capacity, and any one misbehaving client — a runaway script, a scraper, an attacker — can hog all of it and starve every other user. A rate limiter is the small piece of code in front of your endpoints that counts each caller's requests, lets the well-behaved ones through, and turns the rest away with an HTTP 429 and a hint about when it is safe to try again.

Engineers reach for it on any public API where anonymous traffic could hammer the service, on login and password-reset endpoints to blunt credential-stuffing attacks, on expensive endpoints like search or AI inference where each call costs real money, and on tiered pricing where free users get one budget and paid users get another. It is the cheapest, most reliable line of defense between "the service is healthy" and the on-call phone ringing at 3 a.m. It is not a firewall, not a load balancer, not a circuit breaker — those tools shape different problems.

Picture a popular nightclub with one bouncer. Each guest carries a small bucket; every time they enter, a token is dropped in. The bouncer refills each bucket slowly — say one token per second, up to a max of ten. Show up with an empty bucket and you are politely told to come back soon. That is literally the token-bucket algorithm Stripe and AWS use under the hood: bursts are allowed, sustained abuse drains the bucket faster than it refills, and the offender gets held back automatically.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/backend/rate-limiting/
