using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Auth.Web;

/// <summary>Sending the browser to Kale: to sign in, or to sign out there too.</summary>
internal static class SignInFlow
{
    // Set (to the ID token) by SignInCookieEvents when this request found the Kale session ended.
    private const string SessionEndedItem = "Kale.SessionEnded.IdToken";

    // Read by the OpenID Connect handler's sign-out event (KaleSignInSetup), which sends it as id_token_hint.
    internal const string SignOutIdTokenItem = "Kale.SignOut.IdToken";

    /// <summary>Remembers, for the rest of this request, that the user's Kale session has ended.</summary>
    public static void MarkSessionEnded(HttpContext context, string idToken) => context.Items[SessionEndedItem] = idToken;

    /// <summary>
    /// Sends the browser to Kale's sign-in, back to <paramref name="returnUrl"/> afterwards. If this request found the
    /// Kale session ended, it signs out here first (only here), and Kale asks for the password.
    /// </summary>
    public static async Task SignInAsync(HttpContext context, string returnUrl)
    {
        var endedIdToken = context.Items[SessionEndedItem] as string;
        var sessionEnded = endedIdToken is not null;
        if (sessionEnded)
        {
            // Only this application's sign-in ends. Kale's own sign-in cookie stays, so the user's other
            // applications keep signing them in without a password.
            await context.SignOutAsync(KaleSignInSetup.CookieScheme);

            // The marker holds the ID token, encrypted and signed with Data Protection (the browser can't read or
            // forge it): with the sign-in gone, the user's own sign-out still needs it as id_token_hint to end the
            // Kale sign-in too (a shared computer). An ID token is about a kilobyte, well within a cookie's 4 KB.
            var (name, options) = PasswordMarker(context);
            context.Response.Cookies.Append(name, MarkerProtector(context).Protect(endedIdToken!), options);
        }

        // prompt=login: Kale's own cookie would otherwise sign the user straight back in without a password, and
        // that new session would end their session on the other device (one session per user and application).
        var properties = new OpenIdConnectChallengeProperties { RedirectUri = returnUrl };
        if (sessionEnded || context.Request.Cookies.ContainsKey(PasswordMarker(context).Name))
        {
            properties.Prompt = OpenIdConnectPrompt.Login;
        }

        await context.ChallengeAsync(KaleSignInSetup.OpenIdConnectScheme, properties);
    }

    /// <summary>A sign-in has completed: the next one may use Kale's own cookie again.</summary>
    public static void ForgetPasswordMarker(HttpContext context)
    {
        var (name, options) = PasswordMarker(context);
        if (context.Request.Cookies.ContainsKey(name))
        {
            context.Response.Cookies.Delete(name, options);
        }
    }

    // Remembers "the next sign-in asks for the password" after the sign-in cookie is gone. The redirect to Kale
    // already carries prompt=login, but the user may leave Kale's page and come back later: without this, that
    // sign-in would go through Kale's own cookie. Named after the sign-in cookie (whose name holds the application
    // key, so applications on one host don't share it) and with its settings (httpOnly, SameSite, Secure).
    private static (string Name, CookieOptions Options) PasswordMarker(HttpContext context)
    {
        var cookie = context.RequestServices.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(KaleSignInSetup.CookieScheme).Cookie;
        var options = cookie.Build(context);
        options.MaxAge = KaleSignInSetup.SignInLifetime;
        return ($"{cookie.Name}.ask-password", options);
    }

    private static IDataProtector MarkerProtector(HttpContext context) =>
        context.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("Kale.Auth.Web.AskPasswordMarker");

    // The ID token kept in the marker, or null (no marker, or one this application can't read: keys rotated, or
    // forged).
    private static string? IdTokenFromPasswordMarker(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(PasswordMarker(context).Name, out var marker))
        {
            return null;
        }

        try
        {
            return MarkerProtector(context).Unprotect(marker);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// Signs out here (the cookie) and at Kale, which ends the session and clears Kale's own sign-in cookie, then
    /// sends the browser back to <paramref name="returnUrl"/>. Without an ID token, only here. For the user's own
    /// sign-out.
    /// </summary>
    public static async Task SignOutEverywhereAsync(HttpContext context, string? idToken, string returnUrl)
    {
        await context.SignOutAsync(KaleSignInSetup.CookieScheme);
        ForgetPasswordMarker(context);
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

    /// <summary>
    /// The ID token of the signed-in user, or of the one whose session this request found ended, or of the one whose
    /// ended session left the "ask for the password" marker.
    /// </summary>
    public static async Task<string?> IdTokenAsync(HttpContext context)
    {
        var signedIn = await context.AuthenticateAsync(KaleSignInSetup.CookieScheme);
        return signedIn.Properties?.GetTokenValue("id_token")
            ?? context.Items[SessionEndedItem] as string
            ?? IdTokenFromPasswordMarker(context);
    }
}
