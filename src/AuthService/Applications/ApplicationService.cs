using System.Buffers.Text;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AuthService.Domain;
using AuthService.Persistence;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthService.Applications;

public sealed record NewApplication(
    string Key, string Name, IReadOnlyList<Uri> RedirectUris, IReadOnlyList<Uri> PostLogoutRedirectUris);

/// <param name="ClientSecret">Shown once. Only its hash is stored, so it can't be shown again.</param>
public sealed record CreatedApplication(Application Application, string ClientSecret)
{
    // A record prints all its members by default; logging this must never leak the secret.
    public override string ToString() => $"CreatedApplication {{ Key = {Application.Key}, ClientSecret = *** }}";
}

/// <summary>
/// The only code that creates or changes applications and their OpenIddict clients, always together in one
/// transaction (docs/design/phase-3-openiddict.md). OpenIddict's stores use the same AuthDbContext, so its
/// writes are part of that transaction.
/// </summary>
public sealed partial class ApplicationService(
    AuthDbContext db, IOpenIddictApplicationManager clients, IHostEnvironment environment)
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

        var application = new Application { Key = request.Key, Name = request.Name };
        db.Applications.Add(application);
        await db.SaveChangesAsync(cancellationToken);

        var secret = NewSecret();
        await clients.CreateAsync(ClientFor(request, secret), cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new CreatedApplication(application, secret);
    }

    /// <summary>Replaces the client secret. The old one stops working immediately.</summary>
    public async Task<string> RotateSecretAsync(Guid applicationId, CancellationToken cancellationToken = default)
    {
        var client = await FindClientAsync(applicationId, cancellationToken);
        var secret = NewSecret();
        await clients.UpdateAsync(client, secret, cancellationToken);
        return secret;
    }

    /// <summary>
    /// Inactive applications keep their client unchanged; the token and authorization handlers refuse them,
    /// so the active flag in <c>applications</c> stays the single source of truth.
    /// </summary>
    public async Task SetActiveAsync(Guid applicationId, bool isActive, CancellationToken cancellationToken = default)
    {
        var application = await db.Applications.SingleAsync(a => a.Id == applicationId, cancellationToken);
        application.IsActive = isActive;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<object> FindClientAsync(Guid applicationId, CancellationToken cancellationToken)
    {
        var key = await db.Applications
            .Where(a => a.Id == applicationId)
            .Select(a => a.Key)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"No application with id {applicationId}.");
        return await clients.FindByClientIdAsync(key, cancellationToken)
            ?? throw new InvalidOperationException($"Application '{key}' has no OpenIddict client.");
    }

    // Every client gets the same fixed rules: the flows from DESIGN.md, and PKCE always required.
    private static OpenIddictApplicationDescriptor ClientFor(NewApplication request, string secret)
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
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.ClientCredentials,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };

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

    private void Validate(NewApplication request)
    {
        if (!KeyFormat().IsMatch(request.Key))
        {
            throw new ValidationException(
                $"'{request.Key}' is not a valid key: 2-63 lowercase letters, digits or dashes, starting with a letter.");
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ValidationException("The name is required.");
        }

        foreach (var uri in request.RedirectUris.Concat(request.PostLogoutRedirectUris))
        {
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
