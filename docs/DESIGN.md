# Task: central authentication/authorization service + thin shared client package (C#)

Start in **Plan mode**. Read this whole file, then propose a plan and wait for my approval before writing any code. Ask me about the "Open items" before planning the parts they affect.

## My environment and how to work with me
- macOS. I have only **.NET SDK** and **VS Code** installed. You must check for and set up everything else (git, Homebrew, Podman, podman compose provider, `dotnet-ef`, `grpcurl`, etc.).
- Before installing anything, tell me what you will install and why. Anything that needs `sudo` or an interactive password: don't try it, give me the exact command to run myself.
- I have not used Claude Code before. Explain briefly what you are about to do before each phase, and keep explanations short and concrete.
- Check the CPU architecture (Apple Silicon vs Intel) and make sure every container image works on it.

## Problem
I have several C# APIs that each contain a copy-pasted auth implementation. A bug fix has to be applied in every copy. I want one central auth service plus a small shared NuGet package that client apps reference.

## Decided architecture
- **Central auth service** owns users, roles, groups, applications, sessions and audit data. Client apps never read its database.
- **Shared NuGet package (`Auth.Client`)** holds only plumbing, no data: authorize attribute, policy provider, token validation, startup registration client, cache, and invalidation listener.
- Users and groups are **global**. Operations and roles are **scoped to an Application**.

## Technology constraints (open source only, everything containerized with Podman)
- **Everything must be open source.** Don't introduce proprietary or source-available components. If a library or product has a non-OSS license (or a license change), flag it and propose an OSS alternative.
- **Database:** PostgreSQL via EF Core with the Npgsql provider. Use `timestamptz` for times. Composite FKs, unique constraints and cross-application checks must be real database constraints.
- **Containers: Podman only**, no Docker-specific features or assumptions. On macOS this means a Podman machine (VM); set it up and verify it works.
  - Each deployable gets a `Containerfile` (multi-stage build, non-root user). Run rootless.
  - One `compose.yaml` that works with `podman compose` starts: PostgreSQL, the message broker, AuthService, SampleApp, and a reverse proxy only if needed.
  - Named volumes for PostgreSQL data. Podman secrets (not plain environment variables in the compose file) for the DB password and client secrets.
  - Healthchecks, and `depends_on` with health conditions so startup order is deterministic.
  - No hardcoded ports or hostnames; use an `.env.example`.
- **Cache:** in-memory for the first version. If a shared cache across instances becomes necessary, use **Valkey** (BSD), not Redis (license changes).
- **Secrets in production:** keep a pluggable secret-provider abstraction. OpenBao (open source Vault fork) may be evaluated later; don't implement it now.
- **gRPC between services:** the AuthService must support TLS. Plain HTTP/2 (h2c) is acceptable only inside the local compose network and must be clearly marked dev-only in config. Tell me what you choose for local dev.

## Authentication layer: use OpenIddict (decided)
- Use **OpenIddict** (Apache 2.0) as the OpenID Connect / OAuth 2.0 server. Pair it with **ASP.NET Core Identity** (MIT) underneath for password hashing, lockout and MFA, with my `User` entity built on it. Confirm this pairing in your plan, or tell me why not.
- Flows: **authorization code + PKCE** for user sign-in from client apps; **client credentials** for app-to-service calls (operation registration, session and role lookups).
- Access tokens: **signed JWTs**, not encrypted, not reference tokens, so apps validate locally using the JWKS endpoint. Keys are generated and rotated inside the auth service.
- **Role selection** is a custom step in the authorization endpoint, after authentication and before the code is issued (see sign-in flow).
- Don't rely on `jti` as the session id (OpenIddict controls it). Put the session id in a custom claim (`sid`).
- Design question for the plan: OpenIddict keeps its own application/authorization/token tables. Decide how its application registration relates to my `Application` entity so there is a single source of truth, and show me before implementing.
- **If you find any real blocker or major downside to OpenIddict for this design, stop and tell me before proceeding.** Otherwise don't reopen this decision.

## Data model
- Application: Id, Key (public unique name like "billing"), Name, IsActive, client credentials (client id = key; secret generated once, shown once, stored only as a hash)
- Operation: Id, ApplicationId, Name, IsObsolete. Unique (ApplicationId, Name)
- OperationImplication: OperationId, ImpliedOperationId
- Role: Id, ApplicationId, Name. Unique (ApplicationId, Name)
- RoleOperation (N-N). Composite FKs must make the DB reject mixing operations and roles from different applications
- User (global), Group (global)
- UserGroup (N-N), UserRole (N-N, direct assignment), GroupRole (N-N)
- UserApplication: UserId, ApplicationId, DefaultRoleId, IsActive
- Session: Id, UserId, ApplicationId, RoleId, Status, ExpiresAt

## Operations model
- Operations are defined as an **enum in each client app's code**. Admins cannot create or edit operations; they only assign them to roles.
- `[Implies(...)]` on an enum member declares that holding it also grants the implied operations (e.g. `InvoiceManage` implies `InvoiceRead`). The service computes the transitive closure when building a role's effective operations, so endpoints only declare the lowest operation they need.
- The attribute and `[Authorize(SomeEnum.Value)]` live in the shared package and must work with any enum type (generic attribute or `Enum` with startup validation).
- Multiple operations in one attribute mean **OR**. Stacked attributes mean AND.
- Implement authorization with ASP.NET Core **policy-based authorization** (custom policy provider + requirement) so it works for controllers, minimal APIs and gRPC.
- Operation names are stored as strings prefixed with the app key (`billing.InvoiceRead`). Never store enum integer values.

## Registration flow
1. A platform admin creates the Application in the auth service. The service generates a client secret, shows it once, stores a hash.
2. The client app keeps the secret in its secret store (never in source control).
3. On startup the app reflects over its enum and sends `{ name, implies[] }` per operation to the service, authenticated with its client credentials. The service upserts under that application only, marks operations that are no longer sent as obsolete, never deletes them, and rejects registrations for any other application.
4. Registration must be idempotent (multiple instances may start at once).
5. An admin then creates roles and assigns operations, users and groups. With no roles assigned, nobody has any permission.

## Sign-in and session flow
1. Client app redirects the user to the auth service, identifying itself by application key.
2. User authenticates.
3. Eligible roles = (direct roles ∪ roles from the user's groups) filtered to that application. The user picks one, or the service uses `DefaultRoleId`, or the only role. Group-derived roles can be chosen.
4. The service creates a `Session(UserId, ApplicationId, RoleId)` and issues a short-lived access token (5-15 min) plus a refresh token. Refreshing must re-check that the session is still active.
5. The token contains only: session id (`sid`), user id, expiry, audience = application. **No operations in the token.**
6. Apps validate the signature locally via JWKS and must reject tokens whose audience is not their own.
7. A user signed in to two apps has two independent sessions.

## Per-request check in a client app
1. Validate token signature, expiry, audience.
2. Check session status (cached, short TTL ~15-30 s).
3. Look up the session's role's **effective operations** (cached per role, implications already expanded).
4. Evaluate the attribute against that set.

Client apps fetch session status and role operations from the auth service over **gRPC**. Admins must be able to stop a session even while the token is valid. Editing a role must take effect on active sessions immediately.

## Cache invalidation
The auth service publishes events (session stopped, role edited, user disabled) to a message queue. Client apps evict the matching cache entries on receipt. Short TTLs are the fallback if an event is lost. Use the **outbox pattern** for publishing.

## Open items: ask me before implementing the affected parts
1. **Message broker (OSS only):** compare RabbitMQ, NATS and Kafka for this use case (small, low-volume invalidation events, outbox publishing, at-least-once delivery) and recommend one. Exclude cloud-only brokers.
2. **Delegation:** should an app's admins manage only their app's roles while a platform admin manages users, groups and applications? If yes, model it with operations on the auth service, which is itself registered as an Application.
3. **Nested groups:** undecided. Recommend whether to support them.
4. **Rename safety** for enum members (optional `[PreviousName]` attribute?).
5. **Audit logging** of role switches and permission changes (not required yet; design so it can be added).
6. **Admin UI:** how much of an admin UI to build in the first version versus API-only.

## Working rules
- Solution layout: `AuthService` (ASP.NET Core + EF Core + OpenIddict), `Auth.Contracts` (gRPC .proto files), `Auth.Client` (shared NuGet package), `SampleApp` (a minimal API consuming the package, used to verify end to end).
- Define the `.proto` contracts and the entity model first and show them to me before implementing.
- Build in phases:
  0. Environment setup (git, Podman machine, tooling) and `CLAUDE.md`
  1. Data model + migrations on PostgreSQL in a container
  2. Application registration + operation sync
  3. OpenIddict setup, sign-in, role selection, session and token issuing
  4. Client package: attribute, policy provider, validation, cache
  5. Invalidation events through the broker
  6. SampleApp end to end, everything started with `podman compose`
- After each phase, run the build and tests and show me real output, not just "it compiles".
- Write tests for: implication closure, cross-app isolation (DB constraint), audience rejection, session stop with a still-valid token, role edit taking effect immediately, idempotent registration.
- Create a `CLAUDE.md` once the solution exists, with the stack and build/test/run commands.
- Commit to git at the end of each phase with a clear message.
- Don't make assumptions on the open items; ask.