# Phase 3 proposal: OpenIddict and our `Application`

Status: **proposal, waiting for approval.** DESIGN.md asks to see this before Phase 3 is implemented.

## The question

OpenIddict keeps its own tables: `openiddict_applications`, `_authorizations`, `_scopes` and `_tokens`. We already have `applications`. If both hold the same facts, they will drift apart. So each fact needs exactly one home.

## Proposal: one row each, one owner per fact

Every `Application` has exactly one OpenIddict application (an OAuth client). Our `ApplicationService` is the **only** code that creates or changes either of them, and it changes both in **one database transaction**. OpenIddict's EF Core stores use our `AuthDbContext`, so they take part in that transaction.

| Fact | Lives in | Why there |
| --- | --- | --- |
| Key (`billing`), name, active flag | `applications` | Ours. `Key` is already immutable and DB-enforced |
| OAuth `client_id` | `openiddict_applications` | Always equal to `Application.Key`. Both never change, so the copy can't drift |
| Client secret | `openiddict_applications` (**hash only**) | OpenIddict hashes the secret itself. We generate it, pass it in once, show it once, and never store the plain text |
| Redirect URIs, post-logout URIs | `openiddict_applications` | OpenIddict validates them on every request; keeping them elsewhere would be a second copy |
| Permissions (endpoints, grant types, PKCE requirement) | `openiddict_applications` | Set by `ApplicationService` from fixed rules, never edited by hand |
| Link | `applications.openiddict_application_id` | `UNIQUE` + FK, so the 1:1 link is a database rule, like the rest of our model |

We **don't** set OpenIddict's display name: there's no consent screen, and the name would be a second copy.

## Flows and what each token looks like

| Flow | Client | Token `aud` | Extra claims |
| --- | --- | --- | --- |
| Authorization code + PKCE (user sign-in) | the client app, e.g. `billing` | `billing` | `sid` (session id), `sub` (user id) |
| Client credentials (registration, session/role lookups) | the client app | `auth` (AuthService itself) | none |
| Refresh | the client app | unchanged | Refresh fails if the session isn't ACTIVE (CLAUDE.md definition, including "user still holds the role") |

Access tokens are **signed, not encrypted** JWTs (`DisableAccessTokenEncryption`), so apps validate them locally through JWKS. They last 10 min; sessions last 8 h.

## The authorization endpoint (role selection)

This uses OpenIddict's **passthrough** mode, so the steps run in our own code, in this order:
1. Is the user signed in? If not, show the login page (Identity cookie).
2. Is the application active, and does the user have an active `UserApplication` row? If not, deny.
3. Run `EligibleRoles.ForUserAsync` (#16). If there are none, deny. If the user picked a role, it must be eligible. Otherwise, use the `DefaultRoleId` if it's eligible, or the only role. Otherwise, show the role picker (TR/EN).
4. Create the `Session` (#14) and issue the code with `sid` = session id.

## Inactive applications

`Application.IsActive` stays the single source. Our token-endpoint and authorization handlers reject a client whose application is inactive. We don't delete or change the OpenIddict client, so reactivating it is just flipping one flag.

## Signing and encryption keys: **needs your decision**

DESIGN.md says keys are "generated and rotated inside the auth service". OpenIddict can **use** several keys (it signs with the first and publishes all of them in JWKS), but it doesn't **create, store or rotate** them for you. OpenIddict also needs an *encryption* key, even with access-token encryption off: authorization codes and refresh tokens are always encrypted.

| Option | How | Trade-off |
| --- | --- | --- |
| **A. Keys in PostgreSQL (recommended)** | A `signing_keys` table holding RSA keys, encrypted with ASP.NET Core Data Protection, whose own key ring is also in PostgreSQL. A background job adds a new key every 30 days and retires the old one after the longest token lifetime. On startup, every instance loads the active keys | Works across several instances with no shared files. About one feature's worth of code |
| B. Keys as files or Podman secrets | Mount PEM files; rotate by hand | Simplest, but rotation is manual and outside the service, which goes against DESIGN.md |
| C. Ephemeral keys | OpenIddict creates them in memory at startup | Development only: a restart invalidates every token, and several instances disagree |

## Other decisions in this proposal (tell me if any is wrong)

1. **Exactly one OAuth client per Application** in v1. If an app later needs both a browser client and a server, that's a v2 change.
2. **The admin UI (`admin-ui`) is the OAuth client of the `auth` Application.** Superadmins hold `auth.Admin` through the `Admin` role, as decided earlier.
3. **Identity options are pinned in code** (`IdentitySchemaVersions.Version2`, so no passkeys). The new startup test (#11) fails if they ever add a table without a migration.

## What Phase 3 would deliver (each its own branch, TDD)

1. `feature/openiddict-setup`: OpenIddict + Identity registration, its tables, and the keys from option A or B.
2. `feature/application-service`: create an app with its client in one transaction, show the secret once, rotate the secret, activate and deactivate.
3. `feature/client-credentials`: the token endpoint for apps, with `aud = auth`, plus the gRPC endpoints for #13's contracts.
4. `feature/sign-in-and-role-selection`: passthrough authorize, login and role picker pages (TR/EN), session creation, and the `sid` claim.
5. `feature/refresh-session-check`: refresh re-checks the session, plus the audience-rejection test.
