using System.Buffers.Text;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AuthService.Admin;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Tokens;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthService.Applications;

public sealed record NewApplication(
    string Key, string Name, IReadOnlyList<Uri> RedirectUris, IReadOnlyList<Uri> PostLogoutRedirectUris);

/// <summary>What can change after registration: the key is permanent (client id, audience, operation prefix).</summary>
public sealed record ApplicationChanges(string Name, IReadOnlyList<Uri> RedirectUris, IReadOnlyList<Uri> PostLogoutRedirectUris);

/// <summary>An application with its return addresses (kept on its OpenIddict client).</summary>
/// <param name="OldSecretValidUntil">When the secret before the latest rotation stops working; null when there is
/// none (never rotated, retired, or its time is over).</param>
public sealed record ApplicationDetails(
    Guid Id, string Key, string Name, bool IsActive, IReadOnlyList<Uri> RedirectUris, IReadOnlyList<Uri> PostLogoutRedirectUris,
    bool DesktopSignIn, DateTimeOffset? OldSecretValidUntil);

/// <param name="ClientSecret">Shown once. Only its hash is stored, so it can't be shown again.</param>
public sealed record CreatedApplication(Application Application, string ClientSecret)
{
    // A record prints all its members by default; logging this must never leak the secret.
    public override string ToString() => $"CreatedApplication {{ Key = {Application.Key}, ClientSecret = *** }}";
}

/// <param name="ClientSecret">Shown once, like a new application's.</param>
/// <param name="OldSecretValidUntil">Until when the previous secret still works; null when it was retired at once.</param>
public sealed record RotatedSecret(string ClientSecret, DateTimeOffset? OldSecretValidUntil)
{
    // Like CreatedApplication: logging this must never leak the secret.
    public override string ToString() => $"RotatedSecret {{ ClientSecret = ***, OldSecretValidUntil = {OldSecretValidUntil} }}";
}

/// <summary>
/// The only code that creates or changes applications and their OpenIddict clients, always together in one
/// transaction (docs/design/phase-3-openiddict.md). OpenIddict's stores use the same AuthDbContext, so its
/// writes are part of that transaction.
/// </summary>
public sealed partial class ApplicationService(
    AuthDbContext db, IOpenIddictApplicationManager clients, IHostEnvironment environment, TimeProvider clock,
    ApplicationSecretSettings secretSettings)
{
    // Same rule as the database's ck_applications_key_format; checked here first for a readable error.
    [GeneratedRegex(@"^[a-z][a-z0-9-]{1,62}\z")]
    private static partial Regex KeyFormat();

    public async Task<CreatedApplication> CreateAsync(NewApplication request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        if (await db.Applications.AnyAsync(a => a.Key == request.Key, cancellationToken))
        {
            throw new ValidationException($"An application with key '{request.Key}' already exists.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var application = new Application { Key = request.Key, Name = request.Name.Trim() };
        db.Applications.Add(application);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two creates of the same key at once (e.g. a double click) can both pass the check above; the unique
            // index stops the second, which gets the same answer as if it had come later.
            throw new ValidationException($"An application with key '{request.Key}' already exists.", exception);
        }

        var secret = NewSecret();
        await clients.CreateAsync(ClientFor(request, secret, allowTestTokens: IsDevOrTest), cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new CreatedApplication(application, secret);
    }

    /// <summary>The application with its return addresses, or null when there is none with this id.</summary>
    public async Task<ApplicationDetails?> GetAsync(Guid applicationId, CancellationToken cancellationToken = default)
    {
        var application = await db.Applications.AsNoTracking().SingleOrDefaultAsync(a => a.Id == applicationId, cancellationToken);
        if (application is null)
        {
            return null;
        }

        var client = await ClientOfAsync(application.Key, cancellationToken);
        var desktopClient = await clients.FindByClientIdAsync(DesktopClient.ClientIdOf(application.Key), cancellationToken);
        return new ApplicationDetails(application.Id, application.Key, application.Name, application.IsActive,
            await RedirectUrisAsync(client, cancellationToken), await PostLogoutRedirectUrisAsync(client, cancellationToken),
            DesktopSignIn: desktopClient is not null,
            OldSecretValidUntil: application.OldSecretValidUntil > clock.GetUtcNow() ? application.OldSecretValidUntil : null);
    }

    /// <summary>
    /// Changes the name and the return addresses, on the application and its OpenIddict client together. The
    /// secret and the allowed flows stay. "auth"'s addresses are the admin UI's own and can't change here.
    /// </summary>
    public async Task UpdateAsync(Guid applicationId, ApplicationChanges changes, CancellationToken cancellationToken = default)
    {
        ValidateName(changes.Name);
        ValidateAddresses(changes.RedirectUris.Concat(changes.PostLogoutRedirectUris));
        var name = changes.Name.Trim();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Two edits of one application take turns: the second reads the client only after the first committed.
        // Otherwise it would update the client with a stale concurrency token, which OpenIddict refuses.
        await RowLocks.LockApplicationForUpdateAsync(db, applicationId, cancellationToken);
        var application = await db.Applications.SingleOrDefaultAsync(a => a.Id == applicationId, cancellationToken)
            ?? throw new NotFoundException($"No application with id {applicationId}.");
        var client = await ClientOfAsync(application.Key, cancellationToken);

        // A wrong address here would stop every admin from signing in, with no way back through the admin UI.
        if (application.Key == AuthApplication.Key
            && (!SameAddresses(await RedirectUrisAsync(client, cancellationToken), changes.RedirectUris)
                || !SameAddresses(await PostLogoutRedirectUrisAsync(client, cancellationToken), changes.PostLogoutRedirectUris)))
        {
            throw new ValidationException(
                $"The return addresses of '{AuthApplication.Key}' belong to the admin UI: they are set when Kale is set up (admin bootstrap) and can't be changed.");
        }

        application.Name = name;
        await db.SaveChangesAsync(cancellationToken);

        // Start from the client as it is (its secret hash, flows and requirements stay), then replace the rest.
        var descriptor = new OpenIddictApplicationDescriptor();
        await clients.PopulateAsync(descriptor, client, cancellationToken);
        descriptor.DisplayName = name;
        descriptor.RedirectUris.Clear();
        descriptor.RedirectUris.UnionWith(changes.RedirectUris);
        descriptor.PostLogoutRedirectUris.Clear();
        descriptor.PostLogoutRedirectUris.UnionWith(changes.PostLogoutRedirectUris);
        await clients.UpdateAsync(client, descriptor, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Development/Testing: lets the admin UI's client ("auth") ask for test tokens, on a stack set up before test
    /// tokens existed (new clients get it when created). Locks the application like an edit.
    /// </summary>
    public async Task AllowAdminUiTestTokensAsync(CancellationToken cancellationToken = default)
    {
        if (!IsDevOrTest)
        {
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await RowLocks.LockApplicationForUpdateAsync(db, AuthApplication.Key, cancellationToken);
        var client = await clients.FindByClientIdAsync(AuthApplication.Key, cancellationToken);
        if (client is null || await clients.HasPermissionAsync(client, TestTokens.AdminUiPermission, cancellationToken))
        {
            return;
        }

        var descriptor = new OpenIddictApplicationDescriptor();
        await clients.PopulateAsync(descriptor, client, cancellationToken);
        descriptor.Permissions.Add(TestTokens.AdminUiPermission);
        await clients.UpdateAsync(client, descriptor, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Gives the application a new client secret. The old one keeps working for
    /// <see cref="ApplicationSecretSettings.OldSecretValidFor"/> (or until <see cref="RetireOldSecretAsync"/>), so
    /// running instances don't fail before they're redeployed; <paramref name="retireOldSecret"/> stops it at once
    /// (e.g. after a leak). Only one old secret is kept: rotating again during the overlap drops the one before.
    /// </summary>
    public async Task<RotatedSecret> RotateSecretAsync(
        Guid applicationId, bool retireOldSecret = false, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Takes turns with edits, as UpdateAsync does, so the client is read only after they committed and isn't
        // updated with a stale concurrency token.
        await RowLocks.LockApplicationForUpdateAsync(db, applicationId, cancellationToken);
        var application = await db.Applications.SingleOrDefaultAsync(a => a.Id == applicationId, cancellationToken)
            ?? throw new NotFoundException($"No application with id {applicationId}.");
        var client = await ClientOfAsync(application.Key, cancellationToken);

        if (retireOldSecret)
        {
            application.OldSecretHash = null;
            application.OldSecretValidUntil = null;
        }
        else
        {
            // The current secret's hash as OpenIddict stored it (PBKDF2 with its own salt and settings), so the
            // old secret is checked exactly as OpenIddict checks the current one, and never kept in plain text.
            application.OldSecretHash = ((OpenIddictEntityFrameworkCoreApplication<Guid>)client).ClientSecret;
            application.OldSecretValidUntil = clock.GetUtcNow() + secretSettings.OldSecretValidFor;
        }

        await db.SaveChangesAsync(cancellationToken);

        var secret = NewSecret();
        await clients.UpdateAsync(client, secret, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new RotatedSecret(secret, application.OldSecretValidUntil);
    }

    /// <summary>Stops the old secret at once (every instance runs with the new one). Nothing to do if there is none.</summary>
    public async Task RetireOldSecretAsync(Guid applicationId, CancellationToken cancellationToken = default)
    {
        var application = await db.Applications.SingleOrDefaultAsync(a => a.Id == applicationId, cancellationToken)
            ?? throw new NotFoundException($"No application with id {applicationId}.");
        application.OldSecretHash = null;
        application.OldSecretValidUntil = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Inactive applications keep their client unchanged; the token and authorization handlers refuse them,
    /// so the active flag in <c>applications</c> stays the single source of truth.
    /// </summary>
    public async Task SetActiveAsync(Guid applicationId, bool isActive, CancellationToken cancellationToken = default)
    {
        var application = await db.Applications.SingleOrDefaultAsync(a => a.Id == applicationId, cancellationToken)
            ?? throw new NotFoundException($"No application with id {applicationId}.");

        // The admin UI signs in to "auth": deactivating it would lock every admin out, with nobody left to undo it.
        if (application.Key == AuthApplication.Key && !isActive)
        {
            throw new ValidationException($"The application '{AuthApplication.Key}' can't be deactivated: the admin UI depends on it.");
        }

        application.IsActive = isActive;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Switches the application's desktop client (<see cref="DesktopClient"/>) on or off: creates or deletes it, in
    /// the same transaction as the application's lock. Off deletes the client together with its refresh tokens
    /// (OpenIddict deletes a client's tokens with it), so desktop sign-ins can't renew; their access tokens run out
    /// within their 10 minutes. Switching to the current state changes nothing.
    /// </summary>
    public async Task SetDesktopSignInAsync(Guid applicationId, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Takes turns with edits and with another switch, so two switches on at once don't both create the client.
        await RowLocks.LockApplicationForUpdateAsync(db, applicationId, cancellationToken);
        var application = await db.Applications.AsNoTracking().SingleOrDefaultAsync(a => a.Id == applicationId, cancellationToken)
            ?? throw new NotFoundException($"No application with id {applicationId}.");

        // The admin UI is a web application; a desktop client for "auth" would only be another way to get admin tokens.
        if (application.Key == AuthApplication.Key && enabled)
        {
            throw new ValidationException($"The application '{AuthApplication.Key}' can't have desktop sign-in.");
        }

        var client = await clients.FindByClientIdAsync(DesktopClient.ClientIdOf(application.Key), cancellationToken);
        if (enabled && client is null)
        {
            await clients.CreateAsync(DesktopClientFor(application), cancellationToken);
        }
        else if (!enabled && client is not null)
        {
            await clients.DeleteAsync(client, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<object> ClientOfAsync(string key, CancellationToken cancellationToken) =>
        await clients.FindByClientIdAsync(key, cancellationToken)
            ?? throw new InvalidOperationException($"Application '{key}' has no OpenIddict client.");

    private async Task<IReadOnlyList<Uri>> RedirectUrisAsync(object client, CancellationToken cancellationToken) =>
        (await clients.GetRedirectUrisAsync(client, cancellationToken)).Select(uri => new Uri(uri)).ToList();

    private async Task<IReadOnlyList<Uri>> PostLogoutRedirectUrisAsync(object client, CancellationToken cancellationToken) =>
        (await clients.GetPostLogoutRedirectUrisAsync(client, cancellationToken)).Select(uri => new Uri(uri)).ToList();

    // Order doesn't matter; the addresses are compared as written.
    private static bool SameAddresses(IReadOnlyList<Uri> current, IReadOnlyList<Uri> wanted) =>
        current.Select(uri => uri.AbsoluteUri).ToHashSet().SetEquals(wanted.Select(uri => uri.AbsoluteUri));

    // Every client gets the same fixed rules: the flows from DESIGN.md, and PKCE always required.
    private static OpenIddictApplicationDescriptor ClientFor(NewApplication request, string secret, bool allowTestTokens)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = request.Key,
            ClientSecret = secret,
            ClientType = ClientTypes.Confidential,
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.EndSession,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.ClientCredentials,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };

        // The admin UI may ask for test tokens, only in Development/Testing (where the grant type exists at all).
        if (allowTestTokens && request.Key == AuthApplication.Key)
        {
            descriptor.Permissions.Add(TestTokens.AdminUiPermission);
        }

        foreach (var uri in request.RedirectUris)
        {
            descriptor.RedirectUris.Add(uri);
        }

        foreach (var uri in request.PostLogoutRedirectUris)
        {
            descriptor.PostLogoutRedirectUris.Add(uri);
        }

        return descriptor;
    }

    // A public client (no secret: a desktop app can't keep one), so no client credentials: it acts only for a
    // signed-in user. PKCE always required, like every client. Native matters: for a native client OpenIddict (7.7)
    // ignores the port when it compares a loopback return address (RFC 8252 7.3), everything else (scheme, host,
    // path) must match. Without it, only the registered address without a port would be accepted.
    private static OpenIddictApplicationDescriptor DesktopClientFor(Application application) => new()
    {
        ClientId = DesktopClient.ClientIdOf(application.Key),
        ClientType = ClientTypes.Public,
        ApplicationType = ApplicationTypes.Native,
        Permissions =
        {
            Permissions.Endpoints.Authorization,
            Permissions.Endpoints.Token,
            Permissions.Endpoints.EndSession,
            Permissions.GrantTypes.AuthorizationCode,
            Permissions.GrantTypes.RefreshToken,
            Permissions.ResponseTypes.Code,
        },
        Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        RedirectUris = { DesktopClient.RedirectUri },
        PostLogoutRedirectUris = { DesktopClient.PostLogoutRedirectUri },
    };

    /// <summary>
    /// Throws <see cref="ValidationException"/> when <see cref="CreateAsync"/> would refuse the request (it calls
    /// this too), so a caller can check before writing anything else.
    /// </summary>
    public void Validate(NewApplication request)
    {
        if (request.Key is null || !KeyFormat().IsMatch(request.Key))
        {
            throw new ValidationException(
                $"'{request.Key}' is not a valid key: 2-63 lowercase letters, digits or dashes, starting with a letter.");
        }

        ValidateName(request.Name);
        ValidateAddresses(request.RedirectUris.Concat(request.PostLogoutRedirectUris));
    }

    private static void ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ValidationException("The name is required.");
        }

        // The column's limit (ApplicationConfiguration); checked here for a readable 400 instead of a database error.
        // Names are stored trimmed, like role and group names.
        if (name.Trim().Length > MaxNameLength)
        {
            throw new ValidationException($"The name can be at most {MaxNameLength} characters.");
        }
    }

    private const int MaxNameLength = 200;

    private void ValidateAddresses(IEnumerable<Uri?> addresses)
    {
        foreach (var uri in addresses)
        {
            if (uri is null)
            {
                throw new ValidationException("A redirect or post-logout address is missing.");
            }

            // OpenIddict refuses these too (OAuth forbids a fragment in a redirect address), but with its own
            // exception type, after the application row was written.
            if (uri.IsAbsoluteUri && !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new ValidationException($"'{uri}' is not allowed: a redirect address can't have a fragment (#...).");
            }

            // Tokens are sent to these addresses, so they must be exact and encrypted in transit.
            // Plain HTTP only for local development (localhost, 127.0.0.1, ::1), and never in production.
            var isHttps = uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps;
            var isLocalHttp = uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback && IsDevOrTest;
            if (!isHttps && !isLocalHttp)
            {
                throw new ValidationException(
                    $"'{uri}' is not allowed: use an absolute https address (plain http only for localhost).");
            }
        }
    }

    private bool IsDevOrTest => environment.IsDevelopment() || environment.IsEnvironment("Testing");

    // 32 random bytes, base64url: unguessable and safe in configuration files and URLs.
    private static string NewSecret() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
}
