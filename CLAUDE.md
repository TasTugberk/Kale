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

Shared build settings live in `Directory.Build.props`. Package versions live **only** in `Directory.Packages.props` (central package management): add `<PackageVersion>` there and a version-less `<PackageReference>` in the project.

## Commands
```bash
podman machine start          # once after each reboot; containers need the VM
dotnet build                  # warnings are errors
dotnet test
podman compose up -d          # full stack (from Phase 6)
```

## Workflow
- **TDD.** For every behavior: write a failing test first and run it to see it fail for the right reason (red), write the minimum code to pass (green), then refactor with tests green. Show the real red and green output.
- **One branch = one feature** (one cohesive, reviewable change), never a whole phase. Name it `feature/<thing>`, `fix/<thing>`, `chore/<thing>` or `docs/<thing>`. Branch from up-to-date `main`; open one PR per branch.
- **Merge `main` into the branch before opening (or updating) a PR:** `git fetch origin && git merge origin/main`, resolve conflicts, then re-run `dotnet build` + `dotnet test` and push. Several agents and people work in parallel, so a PR must be reviewed against the current `main`, not the one it started from. Merge, don't rebase, so pushed history isn't rewritten.
- The phases in DESIGN.md set the order of work only. Each phase is delivered as several feature branches.

## Rules
- **Open source only.** Do not add MassTransit v9+, MediatR, AutoMapper, FluentAssertions v8+, Redis (use Valkey), or anything else with a non-OSS license. Flag license doubts before adding a package.
- **Podman only.** `Containerfile` per deployable (multi-stage, non-root), rootless, Podman secrets for passwords/client secrets, no hardcoded ports/hosts (`.env.example`).
- Cross-application integrity is enforced by **database constraints** (composite FKs), not only code.
- Operation names are stored as `"{appKey}.{EnumMember}"` strings, never enum integers.
- Access tokens carry `sid`, user id, expiry, audience. No operations.
- Every feature branch ends with a real `dotnet build` + `dotnet test` run before its PR.

## Decisions on the spec's open items
- Broker: RabbitMQ. Fanout exchange, one auto-delete queue per client instance, publisher confirms.
- Admin model: one superadmin level. AuthService registers itself as Application `auth` with operation `auth.Admin`.
- Groups are flat (no nesting). No enum rename support (a rename creates a new operation; the old one becomes obsolete).
- Audit: hooks only (domain events through the outbox); no audit table yet.
- Sign-in requires an active `UserApplication` row.
- Local gRPC uses h2c inside compose only, behind `Grpc:AllowInsecureDevOnly`. TLS is supported.

## PR review
After opening or updating a PR, run the **`pr-reviewer`** agent ([.claude/agents/pr-reviewer.md](.claude/agents/pr-reviewer.md)) with the PR number. It builds and tests the PR in a temporary worktree (skipped for docs/config-only PRs and never done for PRs from forks; the review says when it skipped), checks it against `main`'s version of this file and DESIGN.md, and posts its concerns on the PR as a `COMMENT` review. Fix its **blocking** items (or reply on the PR explaining why not) before asking for a human review.
