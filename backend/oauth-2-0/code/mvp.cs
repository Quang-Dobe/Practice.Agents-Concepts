#:sdk Microsoft.NET.Sdk.Web
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishAot=false
// ^ .NET 10 file-based app. The two properties just re-enable reflection-based JSON,
//   which single-file apps disable by default; nothing to do with OAuth.

// OAuth 2.0 Authorization Code + PKCE, whole dance in one process.
//
// This file plays all four roles at once so the flow is visible without a browser:
//   authorization server (/authorize, /token), resource server (/api/invoices),
//   the client (the driver code at the bottom), and the resource owner (auto-approved).
//
// It proves three things: the code alone is useless without the verifier,
// the code is single-use, and the access token is scope- and audience-checked.

using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

const string Issuer = "http://localhost:5099";
const string ClientId = "demo-client";
const string RedirectUri = "http://localhost:5099/callback"; // registered as an exact string, no wildcards
const string Audience = "https://api.demo";                  // this API only accepts tokens minted for itself

// The authorization server's whole database. Codes die on first use; tokens carry scope + audience.
var codes = new ConcurrentDictionary<string, (string Challenge, string RedirectUri, string Scope)>();
var tokens = new ConcurrentDictionary<string, (string Scope, string Audience, DateTimeOffset ExpiresAt)>();

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders(); // keep stdout to the flow trace only
var app = builder.Build();
app.Urls.Add(Issuer);

// --- Authorization server: front channel. Runs in the browser, so it is attacker-visible. ---
app.MapGet("/authorize", (HttpContext ctx) =>
{
    var q = ctx.Request.Query;
    // Exact redirect_uri match and S256 are both mandatory in OAuth 2.1. "plain" PKCE proves nothing.
    if (q["client_id"] != ClientId || q["redirect_uri"] != RedirectUri || q["code_challenge_method"] != "S256")
        return Results.BadRequest(new { error = "invalid_request" });

    // Real servers authenticate the human and render a consent screen here. We auto-approve.
    var code = NewSecret();
    codes[code] = (q["code_challenge"].ToString(), q["redirect_uri"].ToString(), q["scope"].ToString());

    // state comes back untouched (CSRF defence); iss identifies the AS (mix-up attack defence, RFC 9207).
    return Results.Redirect($"{RedirectUri}?code={code}&state={q["state"]}&iss={Uri.EscapeDataString(Issuer)}");
});

// --- Authorization server: back channel. Server-to-server, never visible to the browser. ---
app.MapPost("/token", async (HttpContext ctx) =>
{
    var form = await ctx.Request.ReadFormAsync();
    // TryRemove is the single-use rule: a second redemption of the same code can never find it.
    if (form["grant_type"] != "authorization_code" || !codes.TryRemove(form["code"].ToString(), out var grant))
        return Results.BadRequest(new { error = "invalid_grant", detail = "unknown or already-used code" });

    // The PKCE check. Only the party that generated the verifier can produce this hash.
    var proof = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString())));
    if (proof != grant.Challenge || form["redirect_uri"] != grant.RedirectUri)
        return Results.BadRequest(new { error = "invalid_grant", detail = "PKCE verifier or redirect_uri mismatch" });

    var accessToken = NewSecret(); // opaque token; a JWT would work identically here
    tokens[accessToken] = (grant.Scope, Audience, DateTimeOffset.UtcNow.AddMinutes(10));
    return Results.Ok(new { access_token = accessToken, token_type = "Bearer", expires_in = 600, scope = grant.Scope });
});

// --- Resource server: validates the bearer token on every call. ---
app.MapGet("/api/invoices", (HttpContext ctx) =>
{
    var bearer = ctx.Request.Headers.Authorization.ToString().Replace("Bearer ", "");
    if (!tokens.TryGetValue(bearer, out var token) || token.ExpiresAt < DateTimeOffset.UtcNow)
        return Results.Unauthorized();
    // Audience stops a token minted for a sibling API from working here (confused deputy).
    if (token.Audience != Audience || !token.Scope.Split(' ').Contains("invoices.read"))
        return Results.StatusCode(403);
    return Results.Ok(new[] { new { id = 42, total = 199.00m } });
});

await app.StartAsync();

// ================= the client =================
// AllowAutoRedirect=false so we can inspect the redirect the browser would have followed silently.
using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });

static string NewSecret() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

async Task<HttpResponseMessage> Redeem(string code, string codeVerifier) =>
    await http.PostAsync($"{Issuer}/token", new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["grant_type"] = "authorization_code",
        ["code"] = code,
        ["redirect_uri"] = RedirectUri, // must be byte-identical to the one sent to /authorize
        ["client_id"] = ClientId,
        ["code_verifier"] = codeVerifier,
    }));

// 1. The client invents a secret, keeps it, and publishes only its hash.
var verifier = NewSecret();
var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
var state = NewSecret();
Console.WriteLine($"1. verifier {verifier[..10]}...  ->  S256 challenge {challenge[..10]}...");

// 2. Front channel. The verifier is NOT sent here — only the challenge.
var authorizeUrl = $"{Issuer}/authorize?response_type=code&client_id={ClientId}"
    + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope={Uri.EscapeDataString("invoices.read")}"
    + $"&state={state}&code_challenge={challenge}&code_challenge_method=S256";
var redirect = await http.GetAsync(authorizeUrl);
var back = QueryHelpers.ParseQuery(redirect.Headers.Location!.Query);
Console.WriteLine($"2. GET /authorize -> {(int)redirect.StatusCode} redirect with code {back["code"].ToString()[..10]}...");

// 3. Validate before spending the code: state proves this response belongs to our request.
if (back["state"] != state || back["iss"] != Issuer) throw new InvalidOperationException("state/iss mismatch");
Console.WriteLine("3. state + iss verified");

// 4. Back channel. Now the verifier is revealed, over TLS, to the AS only.
var tokenJson = await (await Redeem(back["code"]!, verifier)).Content.ReadFromJsonAsync<JsonElement>();
var accessToken = tokenJson.GetProperty("access_token").GetString()!;
Console.WriteLine($"4. POST /token -> access_token {accessToken[..10]}... scope {tokenJson.GetProperty("scope")}");

// 5. Use it.
http.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);
var api = await http.GetAsync($"{Issuer}/api/invoices");
Console.WriteLine($"5. GET /api/invoices -> {(int)api.StatusCode} {await api.Content.ReadAsStringAsync()}");

// 6. Replaying a spent code fails even with the right verifier.
var replay = await Redeem(back["code"]!, verifier);
Console.WriteLine($"6. replay same code -> {(int)replay.StatusCode} {await replay.Content.ReadAsStringAsync()}");

// 7. The point of PKCE: an attacker who steals a fresh code still cannot redeem it.
var stolen = QueryHelpers.ParseQuery((await http.GetAsync(authorizeUrl)).Headers.Location!.Query);
var attack = await Redeem(stolen["code"]!, NewSecret()); // attacker guesses a verifier
Console.WriteLine($"7. stolen code, wrong verifier -> {(int)attack.StatusCode} {await attack.Content.ReadAsStringAsync()}");

await app.StopAsync();
