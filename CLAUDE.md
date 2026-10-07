# Kale

Central authentication/authorization service plus a thin shared client package.
Full spec: [docs/DESIGN.md](docs/DESIGN.md). Approved decisions on its open items are below.

## Stack
- .NET 10 (SDK pinned in `global.json`), C#, ASP.NET Core
- OpenIddict (OIDC/OAuth server) + ASP.NET Core Identity
- PostgreSQL via EF Core + Npgsql (`timestamptz` everywhere)
- gRPC between client apps and AuthService (`Auth.Contracts` holds the `.proto` files)
- RabbitMQ for cache-invalidation events (outbox pattern, raw `RabbitMQ.Client`)
- Podman + podman-compose only (no Docker). Host is Apple Silicon (arm64)
- Tests: xUnit + Shouldly (+ Testcontainers on Podman)
- Admin UI (Phase 7): Next.js BFF, Turkish + English

## Layout
| Path | What |
|---|---|
| `src/AuthService` | Auth server: EF Core, OpenIddict, Identity, gRPC server, admin REST API, outbox publisher |
| `src/Auth.Contracts` | `.proto` gRPC contracts |
| `src/Auth.Client` | Shared NuGet package: `[Authorize(...)]`, policy provider, token validation, registration, cache, invalidation listener |
| `samples/SampleApp` | Minimal API that consumes `Auth.Client`, used for end-to-end checks |
| `tests/*` | `AuthService.Tests`, `Auth.Client.Tests`, `EndToEnd.Tests` |

Shared build settings live in `Directory.Build.props` (`tests/Directory.Build.props` adds test-only settings: global `Xunit`/`Shouldly` usings, underscores allowed in test names). Package versions live **only** in `Directory.Packages.props` (central package management with transitive pinning): add `<PackageVersion>` there and a version-less `<PackageReference>` in the project. Pin a transitive package there too when two packages pull different versions of it (as with EF Core).

## Commands
```bash
podman machine start          # once after each reboot; containers need the VM
./scripts/dev-db.sh           # local PostgreSQL via compose + user-secrets connection string + migrations (idempotent)
dotnet build                  # warnings are errors
dotnet test
podman compose up -d          # stack from compose.yaml (PostgreSQL now; the rest in Phase 6)
podman compose down           # stop; add -v only if you want to delete the database volume
dotnet ef migrations add <Name> --project src/AuthService --output-dir Persistence/Migrations
dotnet ef database update --project src/AuthService --connection "<AuthDb connection string>"  # design-time factory has no real DB
```

## Local environment
- `compose.yaml` takes every value from `.env` (copy of `.env.example`, git-ignored). The DB password is the Podman secret `kale_postgres_password`, mounted as a file. Never put it in `.env` or compose.
- `scripts/dev-db.sh` creates `.env` and the secret if missing, starts PostgreSQL, waits for its healthcheck, stores the AuthService connection string in .NET user secrets (Development only), and applies migrations.
- podman-compose 1.6 can't mount an external secret under a different name, so the compose secret key equals the Podman secret name.

## Tests
- Database tests use a real PostgreSQL via Testcontainers (`PostgresFixture`, image `postgres:18-alpine`). Put them in `[Collection(UsesPostgres.Name)]`: one container is shared, and each test gets its own fresh database from `CreateDbContext()`.
- Testcontainers reaches Podman through `~/.testcontainers.properties` (machine-specific, not in the repo): `docker.host=unix://<podman socket>` and `ryuk.disabled=true`. Get the socket from `podman machine inspect --format '{{.ConnectionInfo.PodmanSocket.Path}}'`. The Podman machine must be running.
- `MigrationTests.Model_has_no_changes_missing_from_migrations` fails whenever the EF model changes without a migration. `ConfigurationValidationTests.The_apps_database_model_matches_the_migrations` does the same for the context built by the app's own DI, which catches model changes from app setup (e.g. Identity options).
- If a new test passes on its first run, prove it can fail (break the code temporarily) before trusting it.

## Code conventions
- **KISS: write code a human can read and maintain.** Prefer the plain, obvious solution over a clever one, even if it's a few lines longer. Avoid tricks such as casts to reuse an overload, dense LINQ chains, deep generics, reflection, or abstractions with a single user, unless the simple version is clearly worse. A new teammate should understand a method on first read.
- **Comment the why, not the what.** If a line wouldn't be obvious on first look (a workaround, a non-obvious constraint, a security or concurrency reason, a link to a DESIGN.md decision), add a short comment saying *why* it's there. Don't comment code that already explains itself.
- Standard .NET naming: PascalCase for types, methods and properties; camelCase for locals and parameters; `_camelCase` for private fields.
- Database names are snake_case (EFCore.NamingConventions). Ids are UUID v7 (`Guid.CreateVersion7()`); times are `DateTimeOffset` mapped to `timestamptz`.
- Entities with their own id inherit `BaseEntity` (`Id`, `CreatedAt`, `ModifiedAt`), and their configuration inherits `BaseEntityConfiguration<T>` (override `ConfigureEntity`). Pure join tables don't. `AuthDbContext.SaveChanges` sets the timestamps of every `IHasTimestamps` entity (`BaseEntity`, plus `User` and `UserApplication`, which can't inherit it) from the injected `TimeProvider`. Bulk updates and raw SQL bypass it, so they must set `ModifiedAt` themselves. In tests, pass a `FakeTimeProvider` to `PostgresFixture.CreateDbContext(clock)`.
- Entities live in `src/AuthService/Domain` as plain classes. Their EF mapping lives in `Persistence/Configurations`, one `IEntityTypeConfiguration` per entity, applied explicitly in `AuthDbContext`. Name constraints explicitly (`ck_…`, `fk_…_same_application`) so a violation says which rule fired.
- Users use ASP.NET Core Identity through `IdentityUserContext<User, Guid>`, so there are **no Identity role tables**: roles are our own per-application entity. Identity's tables are renamed to plain names (`users`, `user_claims`, ...) before our configurations run. Passkeys are off.
- `OnDelete(Restrict)` becomes `ON DELETE RESTRICT`, which reports SQLSTATE 23001 (restrict_violation), not 23503. Tests that check deletes at the database level call `ChangeTracker.Clear()` first, or EF applies cascade/restrict to tracked rows itself.
- Migrations in `Persistence/Migrations` are marked as generated code in `.editorconfig`, so analyzers skip them.
- The auth database provider is configured only in `AuthDatabaseOptions.UseAuthDatabase`, shared by the app, tests and `dotnet ef`.

## Workflow
- **TDD.** For every behavior: write a failing test first and run it to see it fail for the right reason (red), write the minimum code to pass (green), then refactor with tests green. Show the real red and green output.
- **One branch = one feature** (one cohesive, reviewable change), never a whole phase. Name it `feature/<thing>`, `fix/<thing>`, `chore/<thing>` or `docs/<thing>`. Branch from up-to-date `main`; open one PR per branch.
- **Review loop:** fix every **blocking** and **should-fix** item from `pr-reviewer`. Fix nits only when they're cheap; otherwise reply saying why not. Re-run the reviewer **at most once** after fixes, and only if a blocking item was fixed. **Answer every inline review thread directly** (reply in the thread, not only in a summary comment): say what was fixed and in which commit, or why it won't be fixed, then **resolve** the thread. Use the GraphQL mutations `addPullRequestReviewThreadReply` and `resolveReviewThread`. Concerns that appear only in the review body get answered in one PR comment. Don't open separate PRs for small docs tweaks; batch them into the next related branch.
- **Merge `main` into the branch before opening (or updating) a PR:** `git fetch origin && git merge origin/main`, resolve conflicts, then re-run `dotnet build` + `dotnet test` and push. Several agents and people work in parallel, so a PR must be reviewed against the current `main`, not the one it started from. Merge, don't rebase, so pushed history isn't rewritten.
- The phases in DESIGN.md set the order of work only. Each phase is delivered as several feature branches. This changes only the **cadence** of two DESIGN.md working rules, from per phase to per feature branch: running the build and tests (still showing real output, see the last line of Rules) and committing (still with a clear message).

## Rules
- **Open source only.** Do not add MassTransit v9+, MediatR, AutoMapper, FluentAssertions v8+, Redis (use Valkey), or anything else with a non-OSS license. Flag license doubts before adding a package.
- **Podman only.** `Containerfile` per deployable (multi-stage, non-root), rootless, Podman secrets for passwords/client secrets, no hardcoded ports/hosts (`.env.example`).
- Cross-application integrity is enforced by **database constraints** (composite FKs), not only code.
- Operation names are stored as `"{appKey}.{EnumMember}"` strings, never enum integers.
- Access tokens carry `sid`, user id, expiry, audience. No operations.
- **Show real output, not "it compiles".** Every PR description includes the actual `dotnet build` result (warnings/errors) and `dotnet test` result (passed/failed/total) from the final run on the branch.

## Decisions on the spec's open items
- Broker: RabbitMQ. Fanout exchange, one auto-delete queue per client instance, publisher confirms.
- Admin model: one superadmin level. AuthService registers itself as Application `auth` with operation `auth.Admin`.
- Groups are flat (no nesting). No enum rename support (a rename creates a new operation; the old one becomes obsolete).
- Audit: hooks only (domain events through the outbox); no audit table yet.
- Sign-in requires an active `UserApplication` row.
- An operation's `{appKey}.` prefix is enforced by the database: `operations.(application_id, application_key)` references `applications(id, key)`, plus a `starts_with` check. As a result `Application.Key` can't change once operations use it.
- Local gRPC uses h2c inside compose only, behind `Grpc:AllowInsecureDevOnly`. TLS is supported.

## PR review
After opening or updating a PR, run the **`pr-reviewer`** agent ([.claude/agents/pr-reviewer.md](.claude/agents/pr-reviewer.md)) with the PR number. It builds and tests the PR in a temporary worktree (skipped for docs/config-only PRs and never done for PRs from forks; the review says when it skipped), checks it against `main`'s version of this file and DESIGN.md, and posts its concerns on the PR as a `COMMENT` review. Fix its **blocking** items (or reply on the PR explaining why not) before asking for a human review.
