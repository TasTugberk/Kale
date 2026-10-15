using AuthService.Persistence;
using AuthService.Tokens;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;

namespace AuthService.SignIn;

/// <summary>
/// connect/endsession (sign-out from an application), after OpenIddict has validated the request, including the
/// post-logout address against the client. Ends that application's session; the user's sessions in other
/// applications are independent (DESIGN.md) and stay.
/// </summary>
public static class EndSessionEndpoint
{
    public static void MapEndSessionEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapMethods("connect/endsession", [HttpMethods.Get, HttpMethods.Post], HandleAsync);

    private static async Task<IResult> HandleAsync(
        HttpContext context, AuthDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        // The id_token_hint the app sent, already validated by OpenIddict, tells us which session to end.
        var hint = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        if (Guid.TryParse(hint.Principal?.GetClaim(TokenClaims.SessionId), out var sessionId))
        {
            var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
            session?.End(clock.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
        }

        // Signed out of AuthService too, so the next sign-in to any application asks for the password again.
        await context.SignOutAsync(IdentityConstants.ApplicationScheme);

        // OpenIddict redirects to the validated post_logout_redirect_uri (with state), or to "/" without one.
        return Results.SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    }
}
