using AuthService.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Core;
using OpenIddict.EntityFrameworkCore.Models;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthService.Applications;

/// <summary>
/// OpenIddict's application manager, which also accepts an application's old client secret until it runs out
/// (<see cref="ApplicationService.RotateSecretAsync"/>). OpenIddict keeps one secret hash per client; the old one
/// lives on Kale's <c>applications</c> row. Overriding the manager's secret check is the narrowest place for it:
/// OpenIddict's token endpoint (client credentials, code exchange, refresh) and end-session checks all call it,
/// and everything else stays OpenIddict's own.
/// </summary>
public sealed partial class KaleApplicationManager(
    IOpenIddictApplicationCache<OpenIddictEntityFrameworkCoreApplication<Guid>> cache,
    ILogger<OpenIddictApplicationManager<OpenIddictEntityFrameworkCoreApplication<Guid>>> openIddictLogger,
    IOptionsMonitor<OpenIddictCoreOptions> options,
    IOpenIddictApplicationStore<OpenIddictEntityFrameworkCoreApplication<Guid>> store,
    AuthDbContext db,
    TimeProvider clock,
    ILogger<KaleApplicationManager> logger)
    : OpenIddictApplicationManager<OpenIddictEntityFrameworkCoreApplication<Guid>>(cache, openIddictLogger, options, store)
{
    public override async ValueTask<bool> ValidateClientSecretAsync(
        OpenIddictEntityFrameworkCoreApplication<Guid> application, string secret, CancellationToken cancellationToken = default)
    {
        if (await base.ValidateClientSecretAsync(application, secret, cancellationToken))
        {
            return true;
        }

        // Public clients (desktop sign-in) have no secret at all, so no old one either.
        if (!await HasClientTypeAsync(application, ClientTypes.Confidential, cancellationToken))
        {
            return false;
        }

        // An application's web client has the key as client id.
        var now = clock.GetUtcNow();
        var old = await db.Applications.AsNoTracking()
            .Where(a => a.Key == application.ClientId && a.OldSecretValidUntil > now)
            .Select(a => new { a.OldSecretHash, a.OldSecretValidUntil })
            .SingleOrDefaultAsync(cancellationToken);
        if (old?.OldSecretHash is null)
        {
            return false;
        }

        // OpenIddict's own comparison (PBKDF2, time-constant), as for the current secret.
        if (!await ValidateClientSecretAsync(secret, old.OldSecretHash, cancellationToken))
        {
            return false;
        }

        // So ops can see which applications still run with the old secret before it stops working.
        LogOldSecretUsed(logger, application.ClientId, old.OldSecretValidUntil);
        return true;
    }

    // Static with the logger as a parameter: the class has two loggers (OpenIddict's and its own).
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Application {ClientId} authenticated with its old client secret, which stops working at {OldSecretValidUntil}. Redeploy it with the new secret.")]
    private static partial void LogOldSecretUsed(ILogger logger, string? clientId, DateTimeOffset? oldSecretValidUntil);
}
