using System.Security.Claims;
using AuthService.Persistence;
using AuthService.Tokens;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthService.SignIn;

/// <summary>
/// connect/endsession (sign-out from an application), after OpenIddict has validated the request, including the
/// post-logout address against the client. Ends that application's session; the user's sessions in other
/// applications are independent (DESIGN.md) and stay.
/// </summary>
public static class EndSessionEndpoint
{
    public const string SignedOutPath = "/account/signed-out";

    public static void MapEndSessionEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapMethods("connect/endsession", [HttpMethods.Get, HttpMethods.Post], HandleAsync);

    private static async Task<IResult> HandleAsync(
        HttpContext context, AuthDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        // The app's id_token_hint, validated by OpenIddict, says which user and session to sign out. It's required:
        // without it any site could sign users out with a plain link (logout CSRF).
        // (Without a valid hint OpenIddict returns an empty principal, not null, so check for the subject.)
        var hint = (await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal;
        if (hint?.GetClaim(Claims.Subject) is null)
        {
            return Results.BadRequest("A valid id_token_hint is required to sign out.");
        }

        // Only this browser's own user can be signed out: an ID token that leaked (they travel in URLs) must not
        // let someone else end the session from another browser or a script.
        var signedIn = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var isSameUser = signedIn.Succeeded
            && signedIn.Principal.FindFirstValue(ClaimTypes.NameIdentifier) == hint.GetClaim(Claims.Subject);

        if (isSameUser)
        {
            if (Guid.TryParse(hint.GetClaim(TokenClaims.SessionId), out var sessionId))
            {
                var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
                session?.End(clock.GetUtcNow());
                await db.SaveChangesAsync(cancellationToken);
            }

            // Signed out of AuthService too, so the next sign-in to any application asks for the password again.
            await context.SignOutAsync(IdentityConstants.ApplicationScheme);
        }

        // OpenIddict redirects to the validated post_logout_redirect_uri (with state), or to our signed-out page.
        return Results.SignOut(
            new AuthenticationProperties { RedirectUri = SignedOutPath },
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    }
}
