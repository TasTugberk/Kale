using System.Security.Claims;
using AuthService.Sessions;
using AuthService.Tokens;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
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
        HttpContext context, SessionStopper sessions, CancellationToken cancellationToken)
    {
        // The app's id_token_hint, validated by OpenIddict, says which user and session to sign out. It's required:
        // without it any site could sign users out with a plain link (logout CSRF).
        // (Without a valid hint OpenIddict returns an empty principal, not null, so check for the subject.)
        //
        // What OpenIddict (7.7) has checked by now:
        // - The hint is an ID token this server issued: signature, issuer and token type. An invalid hint isn't
        //   an error to OpenIddict (hints are optional), it just yields no principal, so the check below refuses it.
        // - Not its lifetime: OpenIddict skips lifetime validation for hints on purpose, so an ID token past its
        //   own expiry still works. Apps keep the ID token from sign-in for the whole session, up to 8 h.
        // - That it belongs to the client: with client_id, the hint must name that client as its audience or
        //   presenter. Without client_id, a post_logout_redirect_uri must be registered for one of the hint's
        //   audiences (and that client needs the end-session permission). Otherwise the request is refused (400).
        //   With neither, there's nothing to tie it to, and the user lands on our signed-out page.
        var hint = (await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal;
        if (hint?.GetClaim(Claims.Subject) is null)
        {
            return Results.BadRequest("A valid id_token_hint is required to sign out.");
        }

        // Decision: a valid hint is enough to end the session it names, with or without the kale.signin cookie.
        // That cookie lives 8 h from the password entry, the session 8 h from the sign-in to the application, so
        // the cookie often ends hours earlier; requiring it left the session (and its refresh token) alive after
        // a sign-out that looked successful. The cost: whoever holds a leaked ID token can end that one session
        // (nothing more: no sign-in, no data), which the user notices and fixes by signing in again.
        if (Guid.TryParse(hint.GetClaim(TokenClaims.SessionId), out var sessionId))
        {
            await sessions.StopAsync(sessionId, cancellationToken);
        }

        // Signed out of AuthService too, so the next sign-in to any application asks for the password again. Only
        // when this browser's Kale sign-in is the hint's user: someone else signed in here stays signed in.
        var signedIn = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (signedIn.Succeeded
            && signedIn.Principal.FindFirstValue(ClaimTypes.NameIdentifier) == hint.GetClaim(Claims.Subject))
        {
            await context.SignOutAsync(IdentityConstants.ApplicationScheme);
        }

        // OpenIddict redirects to the validated post_logout_redirect_uri (with state), or to our signed-out page.
        return Results.SignOut(
            new AuthenticationProperties { RedirectUri = SignedOutPath },
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    }
}
