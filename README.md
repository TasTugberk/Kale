# Kale

Kale is a central sign-in and authorization service for a set of applications. Instead of each application carrying its own copy of user, role and permission code, they all ask Kale:

- **AuthService** signs users in (OpenID Connect, with optional two-step sign-in), issues tokens, keeps users, roles, groups and sessions, and serves an admin API and a gRPC API for applications.
- **Applications** declare what users can do as a C# enum (their *operations*) and protect endpoints with `[Authorize(MyOperations.OrderApprove)]`. An administrator decides which roles hold which operations. A change takes effect within about a second, and stopping a session works even while its token is still valid.
- **The admin UI** (Next.js, Turkish and English) is where administrators manage applications, users, roles, groups and sessions.

## The parts

| Path | What |
|---|---|
| `src/AuthService` | The service: sign-in pages, OpenID Connect (OpenIddict), ASP.NET Core Identity, PostgreSQL, the admin API, the gRPC API |
| `src/Auth.Client` | NuGet package `Kale.Auth.Client`, for an application's API |
| `src/Auth.Web` | NuGet package `Kale.Auth.Web`, for a web application's server (sign-in with a cookie, backend for frontend) |
| `src/Auth.Desktop` | NuGet package `Kale.Auth.Desktop`, for desktop applications (WPF) |
| `src/Auth.Contracts` | The gRPC contracts (`Kale.Auth.Contracts`) |
| `admin-ui` | The admin UI |
| `samples` | `SampleApp` (an API), `SampleWeb` (a web front end's server), `SampleDesktop` |
| `tests` | xUnit tests, with PostgreSQL in containers (Testcontainers on Podman) |

## Documentation

- **Connect an application:** [docs/integration](docs/integration/README.md) (quick start and full guide, English and Turkish).
- **Run Kale on a server:** [docs/production](docs/production/guide.en.md) ([Türkçe](docs/production/guide.tr.md)).
- **Take over or join the project:** [docs/handover](docs/handover/handover.en.md) ([Türkçe](docs/handover/handover.tr.md)), and the [domain rules](docs/design/domain-rules.md) in ten minutes.
- **Rules, commands and decisions:** [CLAUDE.md](CLAUDE.md). The original brief is [docs/DESIGN.md](docs/DESIGN.md); everything else is indexed in [docs/README.md](docs/README.md).

## Quick start (local)

You need the .NET SDK (version in `global.json`), Podman with `podman compose`, Node 22 (for the admin UI) and `jq`.

```bash
podman machine start          # once after each reboot
./scripts/dev-stack.sh        # PostgreSQL, AuthService, SampleApp and the admin UI in containers
./scripts/smoke-test.sh       # a real sign-in and allow/deny checks against that stack
dotnet build && dotnet test   # the tests need the Podman machine running
```

`dev-stack.sh` prints the addresses and how to sign in to the admin UI as the first admin. To connect a project of your own to this local Kale, see the [quick start](docs/integration/quickstart.en.md).
