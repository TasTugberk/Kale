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
    /// The role decided at connect/authorize, carried inside the authorization code to the code exchange, where
    /// the session starts with it. It never reaches an access or ID token (DESIGN.md: no roles in tokens).
    /// </summary>
    public const string RoleId = "kale_role_id";

    /// <summary>
    /// The authorization code's claims: the user, the audience and the role to start the session with. No session
    /// yet: it starts only when the code is redeemed (<see cref="StartSession"/>).
    /// </summary>
    public static ClaimsPrincipal ForCode(Guid userId, Guid roleId, string applicationKey, ImmutableArray<string> scopes)
    {
        var principal = ForUser(userId, applicationKey, scopes);
        principal.SetClaim(RoleId, roleId.ToString());
        return principal;
    }

    /// <summary>
    /// At the code exchange: turns the code's claims into the session's. Changes the code's principal itself,
    /// rather than building a new one, so OpenIddict's own claims in it stay (such as the authorization the code
    /// belongs to, which OpenIddict revokes with all its tokens if the code is ever redeemed twice).
    /// </summary>
    public static void StartSession(ClaimsPrincipal fromCode, Guid sessionId)
    {
        fromCode.RemoveClaims(RoleId);
        fromCode.SetClaim(SessionId, sessionId.ToString());
        SetDestinations(fromCode);
    }

    /// <summary>
    /// A user's tokens for one application. DESIGN.md: only the session id, the user id, the expiry and the
    /// audience; no roles and no operations (apps look those up through the session, so changes apply at once).
    /// </summary>
    public static ClaimsPrincipal ForSession(Guid userId, Guid sessionId, string applicationKey, ImmutableArray<string> scopes)
    {
        var principal = ForUser(userId, applicationKey, scopes);
        principal.SetClaim(SessionId, sessionId.ToString());
        SetDestinations(principal);
        return principal;
    }

    private static ClaimsPrincipal ForUser(Guid userId, string applicationKey, ImmutableArray<string> scopes)
    {
        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, userId.ToString());
        identity.SetScopes(scopes);

        // The audience is the application's key, so each app accepts only tokens meant for it.
        identity.SetResources(applicationKey);
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// Who the user is, for the application's own pages ("signed in as ..."): the display name as "name" and the
    /// user name as "preferred_username", in the ID token only. Set at the code exchange and again at each
    /// refresh, so a renamed user's next ID token shows the new name.
    /// </summary>
    public static void SetUserNames(ClaimsPrincipal principal, string displayName, string? userName)
    {
        principal.SetClaim(Claims.Name, displayName);
        principal.SetClaim(Claims.PreferredUsername, userName);
        SetDestinations(principal);
    }

    private static void SetDestinations(ClaimsPrincipal principal) =>
        principal.SetDestinations(claim => claim.Type switch
        {
            Claims.Subject or SessionId => [Destinations.AccessToken, Destinations.IdentityToken],

            // Never in the access token: DESIGN.md allows only the session id, user id, expiry and audience there.
            Claims.Name or Claims.PreferredUsername => [Destinations.IdentityToken],
            _ => [],
        });
}
