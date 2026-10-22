using System.Security.Claims;
using AuthService.Persistence;
using AuthService.Sessions;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthService.Tokens;

/// <summary>
/// connect/token, after OpenIddict has validated the request and authenticated the client (passthrough).
/// Decides what goes into the tokens.
/// </summary>
public static class TokenEndpoint
{
    /// <summary>
    /// The audience of tokens apps use to call AuthService itself (registration, session and role lookups).
    /// Deliberately not an application key: keys can't contain ':' (ck_applications_key_format), so a user's
    /// token for any application, including the admin UI's "auth" application, can never carry this audience.
    /// </summary>
    public const string AuthServiceAudience = "urn:kale:auth-api";

    public static void MapTokenEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("connect/token", HandleAsync);

    private static async Task<IResult> HandleAsync(
        HttpContext context, AuthDbContext db, SessionCheck sessionCheck, TestTokens testTokens, CancellationToken cancellationToken)
    {
        var request = context.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("connect/token was reached without an OpenID Connect request.");

        if (request.IsClientCredentialsGrantType())
        {
            return await ClientCredentialsAsync(request.ClientId!, db, cancellationToken);
        }

        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            return await UserTokensAsync(context, db, sessionCheck, cancellationToken);
        }

        if (request.GrantType == TestTokens.GrantType)
        {
            return await testTokens.IssueAsync(context, request, cancellationToken);
        }

        return Refuse(Errors.UnsupportedGrantType, "This grant type is not supported.");
    }

    // A code or refresh token from the user's sign-in. OpenIddict has checked it (and PKCE); we check that its
    // session is still ACTIVE, so stopping a session, disabling the user or taking the role away ends refreshing.
    private static async Task<IResult> UserTokensAsync(
        HttpContext context, AuthDbContext db, SessionCheck sessionCheck, CancellationToken cancellationToken)
    {
        var principal = (await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal
            ?? throw new InvalidOperationException("OpenIddict validated the grant but returned no principal.");

        var session = Guid.TryParse(principal.GetClaim(TokenClaims.SessionId), out var sessionId)
            ? await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
            : null;
        if (session is null || !await sessionCheck.IsActiveAsync(session, cancellationToken))
        {
            return Refuse(Errors.InvalidGrant, "The session is no longer active.");
        }

        // Same claims as at sign-in (sid, user, audience); OpenIddict issues fresh tokens from them.
        return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    // An app calling AuthService as itself. OpenIddict has already checked its id and secret.
    private static async Task<IResult> ClientCredentialsAsync(string clientId, AuthDbContext db, CancellationToken cancellationToken)
    {
        // applications.is_active is the single source of truth: the client itself stays unchanged.
        var isActive = await db.Applications
            .Where(a => a.Key == clientId)
            .Select(a => a.IsActive)
            .SingleOrDefaultAsync(cancellationToken);
        if (!isActive)
        {
            return Refuse(Errors.InvalidClient, "The application is inactive.");
        }

        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, clientId);
        identity.SetResources(AuthServiceAudience);
        identity.SetDestinations(_ => [Destinations.AccessToken]);

        return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    internal static IResult Refuse(string error, string description) => Results.Forbid(
        new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }),
        [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
}
