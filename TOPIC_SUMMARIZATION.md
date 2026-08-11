# OAuth 2.0

OAuth 2.0 is a protocol that lets one app use another app's API on your behalf without you ever handing over your password. Instead of giving an app your credentials, you get redirected to the service that holds your data, that service asks you directly whether the app may have a specific slice of access, and if you agree it issues the app a short-lived access token scoped to only that slice.

Engineers reach for it whenever a third-party app needs to call an API for a user, when a single-page or mobile app calls its own backend and you want short-lived revocable credentials, or for service-to-service calls with no user involved. It also gives you per-integration revocation — you can cut off one app's access without resetting anyone's password. It is worth knowing what it is not: it answers "may this app do X?", not "who is this person?" — that is OpenID Connect, a thin identity layer on top. It is also not a token format; an access token can be a JWT or an opaque random string and OAuth does not care.

Picture an apartment building with a front desk. A cleaning company wants into your gym locker. You do not give them your house key. You send them to the desk clerk, who asks you in person, then prints a keycard that opens the gym locker only and expires at 6pm. The cleaning company holds the keycard and never learns your key. You are the resource owner, the clerk is the authorization server, the keycard is the access token, and "gym locker only" is the scope.

---

Full notes: https://quang-dobe.github.io/Practice.Agents-Concepts/backend/oauth-2-0/present/index.html
