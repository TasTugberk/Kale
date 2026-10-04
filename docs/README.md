# Kale Documentation

Welcome to the documentation for **Kale**, a .NET application. This folder is the
home for everything a contributor needs to understand, build, run, and extend the
project.

> **Status:** Early stage. Sections marked `TODO` will be filled in as the
> codebase takes shape.

---

## Table of Contents

1. [Overview](#overview)
2. [Prerequisites](#prerequisites)
3. [Getting Started](#getting-started)
4. [Repository Layout](#repository-layout)
5. [Development Workflow](#development-workflow)
6. [Testing](#testing)
7. [Configuration](#configuration)
8. [Contributing](#contributing)
9. [Further Reading](#further-reading)

---

## Overview

Kale is a .NET project. This section should explain:

- **What** Kale does and the problem it solves.
- **Who** it is for (end users, other services, internal teams).
- **How** it is delivered (web API, web app, CLI, background worker, library).

`TODO:` Add a short description and, once available, a high-level architecture
diagram.

## Prerequisites

| Tool | Version | Notes |
| ---- | ------- | ----- |
| [.NET SDK](https://dotnet.microsoft.com/download) | `TODO` (e.g. 8.0 or later) | Check with `dotnet --version` |
| Git | Any recent version | |
| IDE (optional) | Visual Studio, JetBrains Rider, or VS Code with C# Dev Kit | |

## Getting Started

```bash
# Clone the repository
git clone https://github.com/TasTugberk/Kale.git
cd Kale

# Restore dependencies
dotnet restore

# Build
dotnet build

# Run (replace with the startup project once it exists)
dotnet run --project src/<ProjectName>
```

## Repository Layout

The planned structure (update it as projects are added):

```text
Kale/
├── docs/          # Project documentation (you are here)
├── src/           # Application source projects
├── tests/         # Unit and integration test projects
├── .gitignore     # .NET-focused ignore rules
└── Kale.sln       # Solution file (TODO)
```

## Development Workflow

1. Create a feature branch from `main`, for example `feature/<short-description>`
   or `fix/<short-description>`.
2. Make small, focused commits with descriptive messages.
3. Ensure the solution builds and all tests pass locally.
4. Open a pull request against `main` and request a review.
5. Merge once the PR is approved and checks are green.

### Coding Conventions

- Follow the standard
  [.NET coding conventions](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions).
- Run `dotnet format` before committing.
- `TODO:` Add an `.editorconfig` to enforce style automatically.

## Testing

```bash
dotnet test
```

`TODO:` Document the test framework (xUnit / NUnit / MSTest), how tests are
organized, and any coverage expectations.

## Configuration

- App settings live in `appsettings.json` and environment-specific overrides
  (`appsettings.Development.json`, and so on).
- Secrets must **never** be committed. Use
  [User Secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets)
  for local development, or environment variables. `.env` files are already
  ignored by `.gitignore`.

`TODO:` List the required configuration keys and what each one does.

## Contributing

Contributions are welcome. Before opening a pull request:

- [ ] The code builds with no new warnings.
- [ ] Tests are added or updated and pass.
- [ ] Documentation in `docs/` is updated if behavior changed.

## Further Reading

- [.NET documentation](https://learn.microsoft.com/dotnet/)
- [ASP.NET Core documentation](https://learn.microsoft.com/aspnet/core/)
- [GitHub flow](https://docs.github.com/get-started/using-github/github-flow)
