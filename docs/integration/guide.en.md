# Connecting your application to Kale

**English** · [Türkçe](guide.tr.md)

> **New here?** The [quick start](quickstart.en.md) protects an API in five steps. This guide explains everything in depth.

This guide is for developers who want their application to use **Kale** for signing users in and for deciding who may do what. It starts from zero: you don't need to know Kale, OpenID Connect or OAuth before reading it. Every code sample in it was run against a real Kale.

The examples build a small application called **Orders**. It has two parts:

- **Orders.Api**, an ASP.NET Core API whose endpoints are protected by Kale.
- **Orders.Web**, a server-side web front end that signs users in through Kale and calls the API.

Your own application may have only an API, or a different front end; take the parts you need.

## Contents

1. [What Kale does for your application](#1-what-kale-does-for-your-application)
2. [The words you'll meet](#2-the-words-youll-meet)
3. [How the pieces talk to each other](#3-how-the-pieces-talk-to-each-other)
4. [Before you start](#4-before-you-start)
5. [Step 1: Register your application in Kale](#5-step-1-register-your-application-in-kale)
6. [Step 2: Add the Kale.Auth.Client package](#6-step-2-add-the-kaleauthclient-package)
7. [Step 3: List what users can do (operations)](#7-step-3-list-what-users-can-do-operations)
8. [Step 4: Protect your API](#8-step-4-protect-your-api)
9. [Step 5: Run it](#9-step-5-run-it)
10. [Step 6: Give people access](#10-step-6-give-people-access)
11. [Step 7: Sign users in from your web front end](#11-step-7-sign-users-in-from-your-web-front-end)
12. [Other front ends and languages (plain HTTP)](#12-other-front-ends-and-languages-plain-http)
13. [How Kale behaves (what to expect)](#13-how-kale-behaves-what-to-expect)
14. [Going to production](#14-going-to-production)
15. [Troubleshooting](#15-troubleshooting)
16. [Reference](#16-reference)

---

## 1. What Kale does for your application

Kale is a central sign-in and permission service. Instead of every application keeping its own users, passwords and permission rules, they all ask Kale.

- **Kale handles:** user accounts and passwords, the sign-in page (Turkish and English), roles, groups, who can use which application, and sessions (including stopping them).
- **Your application handles:** saying *what* can be done in it (its **operations**, e.g. "read orders", "approve orders") and marking each endpoint with the operation it needs.

An administrator then decides in Kale's admin UI which people get which operations, through roles. Your code never changes when permissions change, and a change takes effect at once, even for users who are signed in at the time.

## 2. The words you'll meet

| Word | Meaning | In the Orders example |
|---|---|---|
| **Application** | Your application as Kale knows it. It has a **key** (a short, permanent name), a **client secret** (its password towards Kale) and the addresses Kale may send users back to. | key `orders` |
| **Operation** | One thing a user can do in your application. You declare them in code; Kale stores them as `{key}.{Name}`. | `orders.OrderRead`, `orders.OrderCreate` |
| **Role** | A named set of operations in one application, made by an administrator. | `Clerk` = `orders.OrderCreate` |
| **Group** | A set of users who all get the same roles (handy for teams). | `Warehouse team` |
| **Access** | Whether a user may use your application at all. Signing in needs active access **and** a role. | |
| **Session** | One sign-in of one user to one application, with one chosen role. It lasts at most 8 hours. | |
| **Access token** | What your front end sends to your API on each request (`Authorization: Bearer …`). It lasts 10 minutes and names the user and the session, nothing else. | |
| **Refresh token** | Used by your front end to get a new access token without asking the user again. Each refresh answer has a new one: always keep the newest. | |

## 3. How the pieces talk to each other

```
 Browser                 Orders.Web                   Kale (AuthService)              Orders.Api
    |   open the site        |                                |                             |
    |----------------------->|  not signed in: go to Kale     |                             |
    |<-----------------------|------------------------------->|                             |
    |   Kale's sign-in page (user name, password; role if several)                          |
    |<------------------------------------------------------->|                             |
    |   back to Orders.Web with a one-time code               |                             |
    |----------------------->|  code + secret -> tokens       |                             |
    |                        |------------------------------->|                             |
    |                        |  GET /orders  (Bearer access token)                          |
    |                        |------------------------------------------------------------->|
    |                        |                                |  is this session active?    |
    |                        |                                |  which operations has its   |
    |                        |                                |  role? (gRPC, cached)       |
    |                        |                                |<----------------------------|
    |                        |  200 with the orders, or 403                                 |
    |                        |<-------------------------------------------------------------|
```

- Orders.Api checks each token's signature itself, and asks Kale (over gRPC) whether the session is still active and which operations its role has. The answers are cached for a short time.
- When an administrator stops a session or edits a role, Kale announces it (over RabbitMQ) and your API forgets its cached answer within about a second. Without RabbitMQ (as for an application you run with `dotnet run` against the local stack, which doesn't publish RabbitMQ to your machine), the cached answers simply run out: at most 20 seconds for a session, 5 minutes for a role's operations.

## 4. Before you start

You need:

- **.NET 10 SDK** (`dotnet --version` shows `10.x`).
- **A running Kale** and its address:
  - **For local development** run the stack from the Kale repository: `podman machine start`, then `./scripts/dev-stack.sh`. Kale is then at `http://auth.kale.localhost:8080/` (sign-in and tokens) and `http://localhost:8081/` (gRPC, local development only). The admin UI is at `http://localhost:8083/`.
  - **In a shared environment** ask the Kale team for its `https://` address.
- **A Kale administrator account** (to register your application and give people roles). Locally that's the user `admin`; its password is shown by `podman secret inspect --showsecret --format '{{.SecretData}}' kale_admin_password`.

## 5. Step 1: Register your application in Kale

Your application needs a **key**, a **name** and the addresses Kale may send users back to.

- **Key:** 2 to 63 characters: lowercase letters, digits and `-`, starting with a letter (e.g. `orders`). It is permanent: it's the client id, the token audience and the prefix of your operations.
- **Sign-in return address:** where your web front end receives users after sign-in. With ASP.NET Core's OpenID Connect handler (step 7) that's `https://<your-site>/signin-oidc`.
- **Sign-out return address:** `https://<your-site>/signout-callback-oidc`.
- Addresses must be `https://`. Plain `http://` is accepted only for `localhost`, and only by a Kale running for local development; a shared Kale accepts `https://` only.

**In the admin UI:** open **Applications**, fill in **New application** and press **Create**. The page then shows the **client secret once**: copy it now and store it safely. Kale keeps only a hash of it, so it can't show it again; if it's lost, use **New secret** on the application's row (the old secret stops working at once).

**Locally, in one step, once your projects exist:** `./scripts/register-app.sh orders --web http://localhost:5199 <Orders.Api folder> <Orders.Web folder>` registers the application and writes every setting (the secret included) into the user secrets of all the listed projects. You can then skip the rest of this step, and the `dotnet user-secrets` commands in steps 4 and 7. List every project of the application in that one command: running it again later needs `--new-secret`, which replaces the secret everywhere you list. See the [quick start](quickstart.en.md).

**Or locally, with a command:**

```bash
podman exec -i kale-authservice dotnet AuthService.dll admin create-application \
  orders "Orders" http://localhost:5199/signin-oidc http://localhost:5199/signout-callback-oidc
```

It prints only the client secret, so you can pipe it straight into a secret store (step 4).

> The client secret is a password. Never commit it, never put it in `appsettings.json`, an environment variable in a compose file, or a log. Locally use .NET user secrets; in containers use a Podman secret mounted as a file (step 4).

## 6. Step 2: Add the Kale.Auth.Client package

`Kale.Auth.Client` is the package your API uses. It isn't on nuget.org: the Kale team publishes it to your company's NuGet feed with `scripts/publish-client.sh <feed>` (the feed's API key on standard input). From a feed, add the source (`dotnet nuget add source <feed> --name kale --configfile nuget.config`) and the package, and skip the build below. Without a feed, build it from the Kale repository:

**Build the packages** (in the Kale repository):

```bash
dotnet pack src/Auth.Contracts -c Release -o ./packages
dotnet pack src/Auth.Client -c Release -o ./packages
```

This produces `Kale.Auth.Client.<version>.nupkg` and `Kale.Auth.Contracts.<version>.nupkg` (the client needs both).

**Point your solution at them** with a `nuget.config` next to your solution:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="kale" value="/path/to/kale/packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
```

**Create the API project and add the package:**

```bash
dotnet new web -n Orders.Api
cd Orders.Api
dotnet add package Kale.Auth.Client
```

> NuGet caches a package version on your machine. When the Kale team ships a change, they raise the version; if you build the packages yourself after a change, raise `VersionPrefix` in Kale's `Directory.Build.props` first, or NuGet keeps using the copy it already has.

## 7. Step 3: List what users can do (operations)

Write one `enum` with everything a user can do in your application. Each member becomes an operation in Kale (`orders.OrderRead`, ...).

```csharp
// OrderOperations.cs
using Auth.Client;

namespace Orders.Api;

/// <summary>Everything a user can be allowed to do in this application.</summary>
public enum OrderOperations
{
    OrderRead,

    // Whoever may create orders may also read them.
    [Implies(OrderRead)] OrderCreate,

    OrderApprove,
}
```

- **`[Implies(...)]`:** holding this operation also gives the listed ones. Endpoints then only name the lowest operation they need. Implications may chain, but not loop.
- **Names:** English letters (A–Z, a–z), digits and `_`, starting with a letter or `_`, at most 100 characters. Don't add the `orders.` prefix yourself. Turkish letters (ç, ğ, ı, ö, ş, ü) compile, but Kale refuses the whole list: your application then runs with no operations registered, and its log says `AuthService rejected this application's operations: …`.
- **Changing the list:** when your application starts, it sends the list to Kale. New members are added. Members you removed become **obsolete**: roles can't be given them any more, and checks for them fail.
- **Don't rename members.** A rename is a new operation; the old one becomes obsolete and every role loses it. Add the new name, ask an administrator to give it to the roles, then remove the old one.

## 8. Step 4: Protect your API

### Program.cs

```csharp
using Auth.Client;
using Orders.Api;

var builder = WebApplication.CreateBuilder(args);

// Every setting comes from the "Kale" configuration section (appsettings, user secrets, environment). In a
// container, Kale:ClientSecretFile and Kale:RabbitMqPasswordFile point at the mounted secret files instead.
builder.AddKaleAuth<OrderOperations>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

var orders = new List<Order> { new(1, "Coffee beans", Approved: false) };

// Anyone (no [Authorize]).
app.MapGet("/health", () => "OK");

// Users holding orders.OrderRead (or orders.OrderCreate, which implies it).
app.MapGet("/orders", [Authorize(OrderOperations.OrderRead)] () => orders);

app.MapPost("/orders", [Authorize(OrderOperations.OrderCreate)] (NewOrder input) =>
{
    var order = new Order(orders.Count + 1, input.Item, Approved: false);
    orders.Add(order);
    return Results.Created($"/orders/{order.Id}", order);
});

// Both attributes must pass (AND): OrderApprove, and OrderRead (directly or through OrderCreate).
app.MapPost("/orders/{id:int}/approve",
    [Authorize(OrderOperations.OrderApprove)] [Authorize(OrderOperations.OrderRead)] (int id) =>
    {
        var index = orders.FindIndex(o => o.Id == id);
        if (index < 0)
        {
            return Results.NotFound();
        }

        orders[index] = orders[index] with { Approved = true };
        return Results.Ok(orders[index]);
    });

// Who is calling: the user's id ("sub") and their Kale session ("sid").
app.MapGet("/me", [Authorize(OrderOperations.OrderRead)] (HttpContext context) => new
{
    UserId = context.User.FindFirst("sub")?.Value,
    SessionId = context.User.FindFirst("sid")?.Value,
});

app.Run();

record Order(int Id, string Item, bool Approved);
record NewOrder(string Item);
```

**Rules for `[Authorize]`:**

- Use `Auth.Client`'s `[Authorize(...)]` with members of **your** operations enum. (Startup fails if an endpoint uses another enum.)
- **One attribute with several operations means OR:** `[Authorize(OrderOperations.OrderCreate, OrderOperations.OrderApprove)]` lets in users with either.
- **Several attributes mean AND:** every one of them must pass.
- **No attribute means public:** anyone can call the endpoint, even without a token.
- It works the same on controllers and actions:

```csharp
[ApiController]
[Route("orders")]
[Authorize(OrderOperations.OrderRead)]           // every action needs OrderRead...
public class OrdersController : ControllerBase
{
    [HttpGet]
    public IActionResult List() => Ok();

    [HttpPost("{id:int}/approve")]
    [Authorize(OrderOperations.OrderApprove)]    // ...and this one OrderApprove too (AND)
    public IActionResult Approve(int id) => Ok();
}
```

- **Name clash:** ASP.NET Core has its own `[Authorize]` in `Microsoft.AspNetCore.Authorization`. If a file imports that namespace too, the compiler can't tell them apart (error CS0104); leave that `using` out, or write `[Auth.Client.Authorize(...)]`.
- **`[AllowAnonymous]` makes an endpoint public:** it skips Kale's check too. Use it only for endpoints that really need no sign-in.

**Who is calling:** the token has only two claims about the user: `sub` (the user's id in Kale) and `sid` (the session id). It has no name, e-mail, roles or operations. Store your own data about users keyed by `sub`. Kale decides the operations, not the token, so a role change applies at once.

### Settings for local development (appsettings.Development.json)

```json
{
  "Kale": {
    "Authority": "http://auth.kale.localhost:8080/",
    "ApplicationKey": "orders",
    "GrpcAddress": "http://localhost:8081/",
    "AllowInsecureHttpDevOnly": true
  }
}
```

`scripts/register-app.sh` (step 1) writes all of these, the secret included, into the project's user secrets. To set them by hand, the client secret goes into .NET user secrets (on your machine, outside the project folder):

```bash
dotnet user-secrets init
dotnet user-secrets set "Kale:ClientSecret" "<the secret from step 1>"   # not needed if register-app.sh wrote it
```

> `AllowInsecureHttpDevOnly` allows plain `http://` addresses. Your application refuses to start with it outside the `Development` and `Testing` environments.

## 9. Step 5: Run it

```bash
dotnet run --urls http://localhost:5198
```

In the log you should see your operations arriving in Kale:

```
info: Auth.Client.OperationRegistrationService[...]
      Operations registered with AuthService: 3 added, 0 reactivated, 0 obsoleted, 0 unchanged.
```

If Kale isn't reachable yet, the application keeps trying in the background (with growing waits, up to 30 seconds) and serves requests meanwhile; checks fail closed (403) until Kale answers. Calling the API now gives **401** without a token. Users get in once an administrator has given them access (next step).

## 10. Step 6: Give people access

An administrator does this in Kale's admin UI, once your operations are registered:

1. **Roles:** open **Applications**, then **Roles** on your application's row. Create a role under **New role** (e.g. `Clerk`). It then appears in the list: tick the operations it should have (e.g. `orders.OrderCreate`) and press **Save operations**.
2. **Users:** open **Users**, then the user. Under **Applications**, choose your application and press **Give access**. Under **Roles (direct)**, assign the role.
3. **Optionally, groups:** under **Groups**, add the role to a group and the users to the group, instead of assigning roles one by one.

A user can sign in to your application only with **active access** and at least **one role** (direct or through a group). With several roles, the user picks one at sign-in, unless their access has a **default role**.

## 11. Step 7: Sign users in from your web front end

Your front end signs users in with **OpenID Connect** (authorization code + PKCE). Kale's applications are *confidential*: exchanging the code for tokens needs the client secret. That's why this must happen on a **server**: a server-side web app, or a backend-for-frontend. A browser-only app (SPA) can't keep the secret; give it a small backend that does the sign-in and keeps the tokens.

Below is **Orders.Web**, a server-side ASP.NET Core app. It uses the same Kale application as Orders.Api (key `orders`), because Kale's tokens are made for one audience: the application key.

```bash
dotnet new web -n Orders.Web
cd Orders.Web
dotnet add package Microsoft.AspNetCore.Authentication.OpenIdConnect
dotnet user-secrets init
dotnet user-secrets set "Kale:ClientSecret" "<the secret from step 1>"   # not needed if register-app.sh wrote it
```

`appsettings.Development.json`:

```json
{
  "Kale": {
    "Authority": "http://auth.kale.localhost:8080/",
    "ApplicationKey": "orders"
  },
  "OrdersApi": "http://localhost:5198/"
}
```

`Program.cs`:

```csharp
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

var builder = WebApplication.CreateBuilder(args);
var kale = builder.Configuration.GetSection("Kale");

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.Cookie.Name = "orders.signin";
        // Kale sessions last 8 hours; the cookie can't outlive them.
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = false;
    })
    .AddOpenIdConnect(options =>
    {
        options.Authority = kale["Authority"];
        options.ClientId = kale["ApplicationKey"];
        options.ClientSecret = kale["ClientSecret"];
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("offline_access");   // to get a refresh token
        options.SaveTokens = true;             // keeps the tokens in the sign-in cookie
        options.MapInboundClaims = false;      // keep the claim names "sub" and "sid"
    });
builder.Services.AddAuthorization();
builder.Services.AddHttpClient();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// The orders page: calls Orders.Api with the signed-in user's access token.
app.MapGet("/", async (HttpContext context, IHttpClientFactory httpClients) =>
{
    var accessToken = await AccessTokenAsync(context, httpClients, kale);
    if (accessToken is null)
    {
        // The Kale session has ended (stopped by an admin, or the user signed in elsewhere). Sign out here and
        // at Kale too: otherwise Kale's own sign-in cookie in this browser would sign the user straight back in,
        // without a password, ending the other device's session in turn.
        await SignOutEverywhereAsync(context, "/signed-out?reason=session-ended");
        return Results.Empty;
    }

    var api = httpClients.CreateClient();
    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(builder.Configuration["OrdersApi"]!), "orders"));
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    using var response = await api.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();
    return Results.Content(
        $"""
        <h1>Orders</h1>
        <p>API answered {(int)response.StatusCode}</p>
        <pre id="orders">{System.Net.WebUtility.HtmlEncode(body)}</pre>
        <form method="post" action="/logout"><button>Sign out</button></form>
        """, "text/html");
}).RequireAuthorization();

// Ends the session here and at Kale (Kale also clears its own sign-in cookie).
app.MapPost("/logout", (HttpContext context) => SignOutEverywhereAsync(context, "/signed-out"));

app.MapGet("/signed-out", (string? reason) => Results.Content(
    (reason == "session-ended" ? "<p>Your session ended (for example, you signed in on another device).</p>" : "<p>Signed out.</p>")
    + "<a href=\"/\">Sign in again</a>", "text/html"));

app.Run();

// Signs out of this app (its cookie) and of Kale (Kale ends the session and clears its own sign-in cookie, then
// sends the browser back to returnUrl through /signout-callback-oidc).
static async Task SignOutEverywhereAsync(HttpContext context, string returnUrl)
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme, new AuthenticationProperties { RedirectUri = returnUrl });
}

// The access token lasts 10 minutes. A minute before it ends, get a new one with the refresh token (each
// refresh answer has a new refresh token: keep it). A refused refresh means the Kale session has ended.
static async Task<string?> AccessTokenAsync(HttpContext context, IHttpClientFactory httpClients, IConfiguration kale)
{
    var result = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    var properties = result.Properties!;
    var expiresAt = DateTimeOffset.Parse(properties.GetTokenValue("expires_at")!, CultureInfo.InvariantCulture);
    if (expiresAt - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(1))
    {
        return properties.GetTokenValue("access_token");
    }

    var client = httpClients.CreateClient();
    using var response = await client.PostAsync(new Uri(new Uri(kale["Authority"]!), "connect/token"), new FormUrlEncodedContent(
        new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = properties.GetTokenValue("refresh_token")!,
            ["client_id"] = kale["ApplicationKey"]!,
            ["client_secret"] = kale["ClientSecret"]!,
        }));
    if (!response.IsSuccessStatusCode)
    {
        return null;
    }

    using var tokens = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var root = tokens.RootElement;
    var accessToken = root.GetProperty("access_token").GetString()!;
    properties.UpdateTokenValue("access_token", accessToken);
    properties.UpdateTokenValue("refresh_token", root.GetProperty("refresh_token").GetString()!);
    properties.UpdateTokenValue("expires_at", DateTimeOffset.UtcNow
        .AddSeconds(root.GetProperty("expires_in").GetInt32()).ToString("o", CultureInfo.InvariantCulture));

    // Store the new tokens in the cookie.
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, result.Principal!, properties);
    return accessToken;
}
```

Run both (`dotnet run --urls http://localhost:5198` in Orders.Api, `dotnet run --urls http://localhost:5199` in Orders.Web) and open `http://localhost:5199/`. You are sent to Kale's sign-in page, then back to the orders page.

**Things to keep in mind:**

- **Return addresses must match exactly** what's registered in Kale (step 1), including the port.
- **Keep the newest refresh token.** Each refresh answer has a new one; an old one stops working (Kale still accepts it for 30 seconds, so two requests refreshing at the same moment both succeed). Always store the token from the latest answer.
- **A 403 from your API** means "not allowed": the user's role lacks the operation, *or* their session ended a moment ago (until the next refresh notices it). Show a "not allowed" message; don't retry in a loop.
- **When the session has ended** (the refresh is refused), sign out at Kale too, as the sample does. Otherwise Kale's own sign-in cookie in that browser signs the user straight back in without a password, which ends their session on their other device in turn.

## 12. Other front ends and languages (plain HTTP)

Kale speaks standard OpenID Connect, so any language can sign users in. Its discovery document is at `<Kale address>/.well-known/openid-configuration`. Most OpenID Connect libraries need only the Kale address, your key and your secret, set to "authorization code + PKCE", with scopes `openid offline_access`.

The same flow by hand, for the local Kale. Use your own key and return address in place of `orders` and `http://localhost:5199`; the return address must be registered (`register-app.sh ... --web <address>`).

**1. Make a PKCE pair** (a random secret and its SHA-256 hash), and a random `state`:

```bash
verifier="$(openssl rand -base64 48 | tr -d '=+/\n' | cut -c1-64)"
challenge="$(printf '%s' "$verifier" | openssl dgst -sha256 -binary | openssl base64 -A | tr '+/' '-_' | tr -d '=')"
state="$(openssl rand -hex 8)"
```

**2. Send the browser to Kale's sign-in:**

```
http://auth.kale.localhost:8080/connect/authorize
  ?client_id=orders
  &response_type=code
  &scope=openid%20offline_access
  &redirect_uri=http%3A%2F%2Flocalhost%3A5199%2Fsignin-oidc
  &code_challenge=<challenge>
  &code_challenge_method=S256
  &state=<state>
```

After sign-in Kale sends the browser to `http://localhost:5199/signin-oidc?code=...&state=...`. Check that `state` is yours.

**3. Exchange the code for tokens** (on your server; the secret goes in Basic authentication):

```bash
curl -s http://auth.kale.localhost:8080/connect/token -u "orders:<client secret>" \
  -d grant_type=authorization_code -d "code=<code>" \
  --data-urlencode "redirect_uri=http://localhost:5199/signin-oidc" -d "code_verifier=$verifier"
```

The answer has `access_token` (10 minutes), `refresh_token`, `id_token`, `expires_in` (600) and `token_type`.

**4. Call your API** with `Authorization: Bearer <access_token>`.

**5. Refresh** before the access token expires (keep the new refresh token from the answer; the old one stops working after 30 seconds):

```bash
curl -s http://auth.kale.localhost:8080/connect/token -u "orders:<client secret>" \
  -d grant_type=refresh_token -d "refresh_token=<refresh_token>"
```

**6. Sign out:** send the browser to `connect/endsession` with `id_token_hint=<id_token>` and `post_logout_redirect_uri=<a registered sign-out return address>`. Kale ends the session and clears its own sign-in cookie.

**Errors at sign-in** come back to your return address as `?error=...`:

| `error` | Meaning |
|---|---|
| `access_denied` | The user has no active access to your application, or no role in it, or it is deactivated. |
| `login_required` | You sent `prompt=none` and the user isn't signed in at Kale. |
| `account_selection_required` | You sent `prompt=none` and the user has to choose a role. |

**Optional sign-in parameters:**

- **`prompt=login`:** always ask for the password.
- **`prompt=none`:** never show a page; return one of the errors above instead.
- **`max_age=<seconds>`:** ask for the password again if the last sign-in is older than that.

**An API in another language:** `Kale.Auth.Client` is for .NET. For an API written in another language:

- Validate the JWT yourself: the signature against Kale's JWKS (from the discovery document), `iss` = Kale's address (the discovery document's `issuer`), `aud` = your key, `typ` = `at+jwt`, and the lifetime (`exp`, `nbf`).
- Call Kale's gRPC services (`src/Auth.Contracts/Protos`) with a client-credentials token: `SessionService.GetSession` for whether the session is active, and `RoleService.GetEffectiveOperations` for its role's operations.
- Talk to the Kale team before doing this.

## 13. How Kale behaves (what to expect)

| Situation | What happens |
|---|---|
| Access token lifetime | 10 minutes. Refresh it a minute before. |
| Session lifetime | At most 8 hours from sign-in (configurable in Kale), however often you refresh. Then the user signs in again. |
| The user signs in again (another device, another browser) | Their earlier session **in your application** ends; its tokens are refused. Sessions in other applications stay. |
| An administrator stops a session, disables the user, removes their access or role | The session ends. Your API refuses its token within about a second with RabbitMQ; without it (e.g. `dotnet run` against the local stack), within 20 seconds. Refreshing is refused at once. |
| An administrator changes a role's operations | Applies to signed-in users within about a second with RabbitMQ; without it, within 5 minutes. |
| Kale doesn't answer | Answers already cached are used until they run out (20 seconds for a session, 5 minutes for a role's operations). After that your API refuses (403) once Kale hasn't answered for 5 seconds: it never lets people in without Kale's answer. |
| The user has several roles | Kale asks which one at sign-in, unless their access has a default role. One session has one role. To use another role, the user signs in again and picks it. |

## 14. Going to production

- **Kale's address** is `https://` (`Kale:Authority`). Remove `AllowInsecureHttpDevOnly`: your application refuses to start with it outside Development/Testing.
- **gRPC** goes to the same `https://` address by default; leave `Kale:GrpcAddress` unset unless the Kale team gives you another one.
- **RabbitMQ** is required outside Development/Testing: `Kale:RabbitMqUri` is `amqps://<user>@<host>/`, and the password is a secret (`Kale:RabbitMqPasswordFile`). Without it, stopped sessions would keep working until the cache runs out, so the application refuses to start.
- **Secrets as files:** the client secret and the RabbitMQ password are mounted files (Podman secrets), read through `Kale:ClientSecretFile` and `Kale:RabbitMqPasswordFile`, never environment variables.
- **Register your production addresses** in Kale (`https://…/signin-oidc`, `https://…/signout-callback-oidc`). Each environment can have its own Kale application and secret.

Example `appsettings.Production.json` (with no secrets in it):

```json
{
  "Kale": {
    "Authority": "https://auth.example.com/",
    "ApplicationKey": "orders",
    "RabbitMqUri": "amqps://orders@rabbitmq.example.com/",
    "ClientSecretFile": "/run/secrets/orders_client_secret",
    "RabbitMqPasswordFile": "/run/secrets/orders_rabbitmq_password"
  }
}
```

## 15. Troubleshooting

| Symptom | Likely cause |
|---|---|
| Startup fails: "Set KaleAuthOptions.ApplicationKey and ClientSecret." | The secret isn't configured. Locally: `dotnet user-secrets set "Kale:ClientSecret" ...` in the project's folder. (With `dotnet run --no-build`, user secrets set up after the last build aren't seen; run a normal `dotnet run`.) |
| Startup fails: "...must use https (AllowInsecureHttpDevOnly is for local development only)." | An `http://` address without `AllowInsecureHttpDevOnly`. |
| Startup fails about an endpoint's `[Authorize]` | It uses members of another enum than the one given to `AddKaleAuth<T>`. |
| Startup fails: "KaleAuthOptions.AllowInsecureHttpDevOnly is only allowed in Development and Testing." | `AllowInsecureHttpDevOnly` is set outside Development/Testing. Remove it and use `https://` addresses. |
| Log: `AuthService rejected this application's operations: …` | An operation name is invalid (e.g. a Turkish letter, or longer than 100 characters), or `[Implies]` loops. The log says which. No operation is registered until you fix it. |
| Log, repeating: `Couldn't register operations with AuthService; retrying in …` | Kale isn't reachable (check `Kale:GrpcAddress`, locally `http://localhost:8081/`, and that the stack runs), or the client secret is wrong (Kale refuses the application's own sign-in). |
| 401 on every call | No token, an expired one, or one made for another application (its `aud` isn't your key). |
| 403 for a user who should be allowed | Their role lacks the operation (check **Roles** in the admin UI), their session ended, or Kale isn't reachable. |
| Sign-in ends at your site with `error=access_denied` | The user has no active access or no role in your application (step 6), or the application is deactivated. |
| Kale shows an error instead of the sign-in page | The `redirect_uri` doesn't exactly match a registered sign-in return address. |
| `invalid_grant` when refreshing | An old refresh token (replaced more than 30 seconds ago), or the session ended. Sign in again. |

## 16. Reference

**`KaleAuthOptions`** (configuration section `Kale`):

| Setting | Required | Meaning |
|---|---|---|
| `Authority` | yes | Kale's address, e.g. `https://auth.example.com/`. |
| `ApplicationKey` | yes | Your application's key in Kale. |
| `ClientSecret` | yes | Your client secret (from user secrets, or read from `ClientSecretFile`). |
| `GrpcAddress` | no | Kale's gRPC address when it's not `Authority` (locally `http://localhost:8081/`). |
| `RabbitMqUri` | outside Development | `amqps://user@host/` (locally `amqp://`). |
| `RabbitMqPassword` | with `RabbitMqUri` | Read from `RabbitMqPasswordFile` in containers. |
| `AllowInsecureHttpDevOnly` | no | Allows `http://` addresses; Development/Testing only. |

**Kale's endpoints** (relative to its address):

| Path | Used for |
|---|---|
| `.well-known/openid-configuration` | Discovery (endpoints, keys). |
| `connect/authorize` | Starting sign-in in the browser. |
| `connect/token` | Code → tokens, refresh. |
| `connect/endsession` | Sign-out. |

**Token contents** (the ones to use; tokens also carry standard claims such as `iss`, `iat` and `jti`): access token: `sub` (user id), `sid` (session id), `aud` (your key), `exp`, `client_id`, `scope`; type `at+jwt`. ID token: `sub`, `sid`.
