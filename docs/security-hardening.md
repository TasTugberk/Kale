# Security hardening backlog

Status: **backlog** (2026-10-21). Nothing here is approved or scheduled yet. Items marked **decision needed** touch DESIGN.md's open items or CLAUDE.md's decisions, so ask before implementing them.

This list comes from a review of the design (CLAUDE.md, DESIGN.md) and a quick search of the code. It wasn't a full audit or a penetration test. Each item should become its own branch (suggested names below), built with TDD like everything else.

## What's already covered

So the gaps below aren't mistaken for the whole picture:

| Threat | Defense in place |
| --- | --- |
| Stolen or intercepted authorization codes | Code+PKCE with S256 only, confidential clients, exact absolute https redirect URIs |
| Stolen access tokens | 10 min lifetime, no operations in the token, session re-checked on every request (20 s cache, RabbitMQ eviction) |
| Password guessing on one account | Identity lockout: 5 tries, then 15 min (`SignIn/SignInSetup.cs`). Passwords ≥ 12 chars |
| CSRF | Admin API takes bearer tokens only. `kale.signin` cookie is `HttpOnly`, `SameSite=Lax`, `Secure` outside Development/Testing. Razor Pages validate antiforgery tokens on POST by default |
| One application reaching another's data | Composite FKs, `NOT_FOUND` for other applications' items over gRPC |
| Forged tokens | Signed tokens, keys rotated every 30 days, stored encrypted; Data Protection key ring must be certificate-encrypted in production |
| Plain-text transport, leaked secrets | `https`/`amqps`/TLS required outside Development/Testing; secrets as mounted files, never environment variables |
| Vulnerable packages | NuGet audit fails the build |

## Backlog, in priority order

### 1. Multi-factor authentication (MFA): **decision needed**

**Problem.** A stolen password is enough to sign in to every application. AuthService is the single front door, and an admin takeover controls everything (one superadmin level).

**Proposal.** TOTP (authenticator app) through ASP.NET Core Identity, which DESIGN.md already chose for MFA. The `users` table already has Identity's `two_factor_enabled` column and `user_claims`/tokens support, so storage may need little or no migration. Recovery codes for lost devices.

**Decide first.**
- Required for everyone, per application, or only for sessions with `auth.Admin`? Recommendation: required for `auth.Admin` from day one, optional for others.
- Passkeys are off by decision. Revisit them as a second factor (or instead of passwords) here?
- Who can reset a user's MFA (admin API endpoint), and does that stop their sessions (`SessionStopper`)?

**Where.** `Pages/Account/Login.cshtml(.cs)` (second step), `SignIn/UserSignIn.cs`, a new enrolment page, admin API reset endpoint, TR/EN texts in `Resources/SharedText.*.resx`.

**Branch.** `feature/mfa-totp` (then `feature/mfa-admin-reset`).

### 2. Rate limiting

**Problem.** Lockout protects one account at a time. Nothing slows an attacker who tries one common password against many accounts (password spraying). Lockout itself lets anyone lock a real user out for 15 min by typing wrong passwords on purpose.

**Proposal.** ASP.NET Core's built-in rate limiter (`Microsoft.AspNetCore.RateLimiting`, part of the shared framework, MIT; no new package):
- Login POST: per client IP and per user name.
- `connect/token`: per client IP and per `client_id`.
- Admin API: per caller.
- gRPC: per calling application (generous; apps call it on every uncached request).

Return 429 with `Retry-After`. Log refusals.

**Watch out.** Behind a reverse proxy every request has the proxy's IP. Configure `ForwardedHeaders` with the proxy's address as a known proxy, or the limiter either blocks everyone or trusts spoofed `X-Forwarded-For` headers.

**Branch.** `feature/rate-limiting`.

### 3. Security headers on the user-facing pages

**Problem.** No `Content-Security-Policy`, `X-Frame-Options` or HSTS found in the code. Without `frame-ancestors`, another site can embed the login page in an invisible frame and trick users into clicking (clickjacking).

**Proposal.** A small middleware for the Razor pages (`/account/*`) and the OpenIddict endpoints that render pages:
- `Content-Security-Policy: default-src 'self'; frame-ancestors 'none'; base-uri 'none'; object-src 'none'`. The pages already use only `wwwroot/kale.css` with no external fonts or scripts, so `'self'` is enough.
- `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer` (authorize URLs carry `state` and codes).
- HSTS outside Development/Testing (`UseHsts`), unless the reverse proxy in front already sets it; decide where TLS ends.

**Watch out.** Don't add `form-action 'self'`: Chrome applies it to the redirect chain after the login POST, which ends at the application's `redirect_uri` on another origin, so sign-in would break.

**Tests.** App-level test (`AuthServiceApp`) that the login page response carries the headers.

**Branch.** `feature/security-headers`.

### 4. Audit log: **decision needed**

**Problem.** Domain events go through the outbox (`UserDisabled`, `RoleEdited`, `SessionStopped`), but nothing stores them. After an incident there's no record of who changed which role, who signed in from where, or which admin did what.

**Proposal.** An `audit_events` table written in the same `SaveChanges` as the change (like the outbox), append-only for the app's database user. Record at least: sign-in success/failure (user, application, IP), lockout, MFA changes, admin API changes (who, what, before/after), session stops.

**Decide first.** CLAUDE.md says "hooks only, no audit table yet" (DESIGN.md open item 5). Retention period and where logs are shipped (SIEM?) also need an owner.

**Branch.** `feature/audit-log`.

### 5. Availability

**Problem.** Client apps fail closed when AuthService doesn't answer (correct for security). But it means that if AuthService is down or flooded, every application is down.

**Proposal.**
- Run at least two AuthService instances behind a load balancer. Before that, check that everything is instance-safe: outbox publishing is (`FOR UPDATE SKIP LOCKED`), Data Protection keys are shared in the database; **verify** that two instances can't both rotate a key at the same moment (`Keys/KeyRing`).
- PostgreSQL with a replica and tested backups (backups contain password hashes and encrypted keys: protect them like the database).
- Health checks already exist (`GET /health`); add readiness that checks the database.

**Branch.** `chore/multi-instance-check` first, then deployment docs.

### 6. Admin hardening

Builds on items 1 and 4.
- Require MFA for any session holding `auth.Admin`.
- Consider a shorter session lifetime for the `auth` application than the 8 h default.
- Consider limiting the admin API to an internal network or VPN at the proxy.

**Branch.** `feature/admin-hardening`.

### 7. Independent security testing

- Add an OWASP ZAP baseline scan against the dev stack (`scripts/dev-stack.sh`) to CI. ZAP is Apache 2.0 and runs as a container under Podman.
- Before production: an external penetration test of AuthService (sign-in, token, admin API, gRPC).

**Branch.** `chore/zap-baseline`.

## To verify (unknown, not necessarily gaps)

Quick checks, each with a test that proves the behavior:

- **User enumeration:** the login page gives the same message and similar timing for an unknown user, a wrong password, a locked account and an inactive user.
- **Refresh token reuse:** a refresh token can't be used twice, and reusing an old one revokes the chain (OpenIddict's rolling refresh tokens; confirm our settings).
- **Breached passwords:** consider rejecting known-leaked passwords at creation (offline list, since networks may be isolated).
- **Error responses:** no stack traces or internal details outside Development (problem responses, gRPC status details).
- **Log contents:** no passwords, client secrets, tokens or authorization codes in logs.
- **Open redirect:** `connect/endsession` and the role picker only redirect to registered URIs.
