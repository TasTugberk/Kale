using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace Auth.Web;

/// <summary>Sending the browser to Kale: to sign in, or to sign out there too.</summary>
internal static class SignInFlow
{
    // Set (to the ID token) by SignInCookieEvents when Kale refused to renew this request's token.
    private const string SessionEndedItem = "Kale.SessionEnded.IdToken";

    // Read by the OpenID Connect handler's sign-out event (KaleSignInSetup), which sends it as id_token_hint.
    internal const string SignOutIdTokenItem = "Kale.SignOut.IdToken";

    /// <summary>Remembers, for the rest of this request, that the user's Kale session has ended.</summary>
    public static void MarkSessionEnded(HttpContext context, string idToken) => context.Items[SessionEndedItem] = idToken;

    /// <summary>
    /// Sends the browser to Kale's sign-in, back to <paramref name="returnUrl"/> afterwards. If this request found the
    /// Kale session ended, it signs out at Kale first, and then comes back to this same address to sign in.
    /// </summary>
    public static Task SignInAsync(HttpContext context, string returnUrl)
    {
        if (context.Items[SessionEndedItem] is string idToken)
        {
            // Kale's own sign-in cookie would otherwise sign the user straight back in without a password, and that
            // new session would end the user's session on their other device (one session per user and application).
            var request = context.Request;
            var comeBackTo = HttpMethods.IsGet(request.Method) ? $"{request.PathBase}{request.Path}{request.QueryString}" : returnUrl;
            return SignOutEverywhereAsync(context, idToken, comeBackTo);
        }

        return context.ChallengeAsync(KaleSignInSetup.OpenIdConnectScheme, new AuthenticationProperties { RedirectUri = returnUrl });
    }

    /// <summary>
    /// Signs out here (the cookie) and at Kale, which ends the session and clears Kale's own sign-in cookie, then
    /// sends the browser back to <paramref name="returnUrl"/>. Without an ID token, only here.
    /// </summary>
    public static async Task SignOutEverywhereAsync(HttpContext context, string? idToken, string returnUrl)
    {
        await context.SignOutAsync(KaleSignInSetup.CookieScheme);
        if (idToken is null)
        {
            context.Response.Redirect(returnUrl);
            return;
        }

        // Passed explicitly: the OpenID Connect handler would look for it in the cookie, which is gone, or which
        // no longer counts once the session ended.
        context.Items[SignOutIdTokenItem] = idToken;
        await context.SignOutAsync(KaleSignInSetup.OpenIdConnectScheme, new AuthenticationProperties { RedirectUri = returnUrl });
    }

    /// <summary>The ID token of the signed-in user, or of the one whose session this request found ended.</summary>
    public static async Task<string?> IdTokenAsync(HttpContext context)
    {
        var signedIn = await context.AuthenticateAsync(KaleSignInSetup.CookieScheme);
        return signedIn.Properties?.GetTokenValue("id_token") ?? context.Items[SessionEndedItem] as string;
    }
}
