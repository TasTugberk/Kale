using System.Collections.Immutable;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthService.Tokens;

public static class TokenClaims
{
    /// <summary>OpenID Connect's session id claim: our Session's id, which apps send back to check the session.</summary>
    public const string SessionId = "sid";

    /// <summary>
    /// A user's tokens for one application. DESIGN.md: only the session id, the user id, the expiry and the
    /// audience; no roles and no operations (apps look those up through the session, so changes apply at once).
    /// </summary>
    public static ClaimsPrincipal ForSession(Guid userId, Guid sessionId, string applicationKey, ImmutableArray<string> scopes)
    {
        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, userId.ToString());
        identity.SetClaim(SessionId, sessionId.ToString());
        identity.SetScopes(scopes);

        // The audience is the application's key, so each app accepts only tokens meant for it.
        identity.SetResources(applicationKey);
        identity.SetDestinations(claim => claim.Type is Claims.Subject or SessionId
            ? [Destinations.AccessToken, Destinations.IdentityToken]
            : []);
        return new ClaimsPrincipal(identity);
    }
}
