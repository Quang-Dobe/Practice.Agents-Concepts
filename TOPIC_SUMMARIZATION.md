# JWT (JSON Web Token)

A JWT is a small, signed text string that a server hands a client to prove the client has already been authenticated. It carries a little bit of JSON describing who the user is and when the token expires, all bundled into three dot-separated, base64-encoded pieces — a header naming the signing algorithm, a payload of claims, and a signature that seals the first two parts so any change to them is detectable.

It matters because the signature lets any server verify a token without looking anything up in a database. That makes JWT the default building block for stateless REST and gRPC APIs, microservice-to-microservice auth, and single sign-on flows like OpenID Connect. Engineers reach for it when they need to scale horizontally without sticky sessions, and avoid it when they need instant revocation on logout or when a plain server-side session cookie would be simpler. The payload is readable by anyone who has the token, so it carries identity, not secrets.

Think of a JWT as a tamper-evident wristband at a music festival. The gate checks your ID once and snaps the wristband on you with your access tier printed on it. For the rest of the night, no booth re-checks your ID — they just glance at the wristband and check its holographic seal. If anyone scratched out "general admission" and wrote "VIP," the seal would break and the next bouncer would notice instantly. The festival keeps no list of who got a wristband; the wristband itself carries the proof.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/backend/jwt/
