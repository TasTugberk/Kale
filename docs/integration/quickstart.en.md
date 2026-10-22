# Quick start: protect your API with Kale

**English** · [Türkçe](quickstart.tr.md)

Five steps, on your machine. You need the **.NET 10 SDK**, **Podman**, **jq** and a copy of the Kale repository. Everything else (web sign-in, production, other languages) is in the [full guide](guide.en.md).

## 1. Start Kale (once)

In the Kale repository:

```bash
podman machine start          # once after each reboot
./scripts/dev-stack.sh        # PostgreSQL, RabbitMQ, Kale and its admin UI, in containers
```

The admin UI is at <http://localhost:8083/>. Sign in as `admin`; the password is shown by:

```bash
podman secret inspect --showsecret --format '{{.SecretData}}' kale_admin_password
```

## 2. Add the package to your project

Build the package once, in the Kale repository:

```bash
dotnet pack src/Auth.Contracts -c Release -o ./packages
dotnet pack src/Auth.Client -c Release -o ./packages
```

Then, in your project's folder (`/path/to/kale` is your copy of the Kale repository):

```bash
dotnet new nugetconfig
dotnet nuget add source /path/to/kale/packages --name kale --configfile nuget.config
dotnet add package Kale.Auth.Client
```

## 3. Connect your project to Kale

In the Kale repository, with your application's key (here `inventory`) and your project's folder:

```bash
./scripts/register-app.sh inventory /path/to/Inventory.Api
```

This registers the application in Kale and writes everything your project needs (Kale's address, the key, the client secret) into the project's .NET user secrets. Your `appsettings` stay untouched, and the secret never lands in your repository. Add `--web http://localhost:5000` if the application has a web front end at that address.

## 4. Write the code

List what users can do, as an enum:

```csharp
using Auth.Client;

namespace Inventory.Api;

// Everything a user can be allowed to do in this application.
public enum InventoryOperations
{
    StockRead,
    [Implies(StockRead)] StockUpdate,   // whoever may update stock may also read it
}
```

Then, in `Program.cs`, one line to connect, and an attribute on each endpoint to protect:

```csharp
using Auth.Client;
using Inventory.Api;

var builder = WebApplication.CreateBuilder(args);
builder.AddKaleAuth<InventoryOperations>();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/stock", [Authorize(InventoryOperations.StockRead)] () => new[] { "apples: 12" });
app.MapPost("/stock", [Authorize(InventoryOperations.StockUpdate)] () => Results.NoContent());

app.Run();
```

## 5. Run it, and give people access

```bash
dotnet run
```

The log shows your operations arriving in Kale:

```
Operations registered with AuthService: 2 added, 0 reactivated, 0 obsoleted, 0 unchanged.
```

Your endpoints are now protected: without a token they answer **401**.

In the admin UI, decide who may do what:

1. **Applications** → **Roles** on the `inventory` row. Create a role under **New role** (e.g. `Clerk`), then tick its operations (e.g. `inventory.StockUpdate`) and press **Save operations**.
2. **Users** → a user. Under **Applications**, choose `inventory` and press **Give access**. Under **Roles (direct)**, assign `Clerk`.

With that user's token, `/stock` now answers **200**; for someone without the operation, **403**. The next section shows how a user gets a token.

## Calling your API as a user

A request needs the signed-in user's access token (`Authorization: Bearer …`), which your application's web front end gets when the user signs in through Kale:

- **An ASP.NET Core web front end:** see [step 7 of the guide](guide.en.md#11-step-7-sign-users-in-from-your-web-front-end).
- **By hand, to try it out:** register with `--web` (step 3), then follow [the plain HTTP flow](guide.en.md#12-other-front-ends-and-languages-plain-http).

## Next

- Rules for operations and `[Authorize]` (OR, AND, controllers): [guide, steps 3 and 4](guide.en.md#7-step-3-list-what-users-can-do-operations)
- What to expect (token lifetimes, stopped sessions, one session per application): [guide, section 13](guide.en.md#13-how-kale-behaves-what-to-expect)
- Going to production: [guide, section 14](guide.en.md#14-going-to-production)
- Something doesn't work: [troubleshooting](guide.en.md#15-troubleshooting)
