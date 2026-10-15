using System.Security.Claims;
using AuthService.Persistence;
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
    /// <summary>The audience of tokens meant for AuthService itself (registration, session and role lookups).</summary>
    public const string AuthServiceAudience = "auth";

    public static void MapTokenEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("connect/token", HandleAsync);

    private static async Task<IResult> HandleAsync(HttpContext context, AuthDbContext db, CancellationToken cancellationToken)
    {
        var request = context.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("connect/token was reached without an OpenID Connect request.");

        if (request.IsClientCredentialsGrantType())
        {
            return await ClientCredentialsAsync(request.ClientId!, db, cancellationToken);
        }

        return Refuse(Errors.UnsupportedGrantType, "This grant type is not supported yet.");
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

    private static IResult Refuse(string error, string description) => Results.Forbid(
        new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }),
        [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
}
