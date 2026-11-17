using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace Auth.Web;

/// <summary>
/// What the sign-in cookie does beyond ASP.NET Core's defaults: keeps the access token fresh on every request,
/// signs the user out when Kale refuses (the Kale session has ended), and answers scripts with status codes
/// instead of redirects.
/// </summary>
internal sealed class SignInCookieEvents(TokenRenewal renewal, TimeProvider clock) : CookieAuthenticationEvents
{
    /// <summary>Runs on every request that brings the cookie, before the endpoint (so the token can be renewed in time).</summary>
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        // Against cross-site request forgery: the browser sends the cookie with every request to this site, and
        // SameSite=Lax still lets requests from sibling subdomains through. A request that changes something
        // counts as signed in only when it comes from this site's own pages.
        if (!IsSafeMethod(context.Request.Method) && !SignInRules.IsSameOrigin(context.Request))
        {
            context.RejectPrincipal();
            return;
        }

        var properties = context.Properties;
        var refreshToken = properties.GetTokenValue("refresh_token");
        if (refreshToken is null)
        {
            // A cookie without tokens can't call anything: sign in again.
            context.RejectPrincipal();
            return;
        }

        var now = clock.GetUtcNow();
        if (!SignInRules.NeedsRenewal(properties.GetTokenValue("expires_at"), now))
        {
            return;
        }

        var result = await renewal.RenewAsync(refreshToken);
        switch (result.Outcome)
        {
            case RenewalOutcome.Renewed:
                properties.UpdateTokenValue("access_token", result.AccessToken!);
                properties.UpdateTokenValue("expires_at", result.ExpiresAt.ToString("o", CultureInfo.InvariantCulture));
                if (result.RefreshToken is not null)
                {
                    properties.UpdateTokenValue("refresh_token", result.RefreshToken);
                }

                if (result.IdToken is not null)
                {
                    properties.UpdateTokenValue("id_token", result.IdToken);
                }

                // ShouldRenew writes the cookie again, valid for as long as it had left counted from IssuedUtc.
                // Moving IssuedUtc to now keeps its original end: a Kale session's 8 hours don't slide.
                properties.IssuedUtc = now;
                context.ShouldRenew = true;
                break;

            case RenewalOutcome.Refused:
                // The cookie stays until a page is opened (SignInFlow signs out at Kale with its ID token then);
                // this request counts as not signed in: a script gets 401, a page goes through Kale's sign-out.
                context.RejectPrincipal();
                if (properties.GetTokenValue("id_token") is { } idToken)
                {
                    SignInFlow.MarkSessionEnded(context.HttpContext, idToken);
                }

                break;

            case RenewalOutcome.Unreachable:
                // Kale didn't answer: that says nothing about the session. Keep the current token (it may have up to
                // a minute left); the next request tries again.
                break;
        }
    }

    /// <summary>Not signed in: a script gets 401 so the front end can react; a page goes to Kale's sign-in.</summary>
    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        if (!SignInRules.IsPageNavigation(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        // Kale has just refused to sign the user in and sent them back here. Asking again would loop: Kale's own
        // cookie signs them in without a password and Kale refuses again at once. The page gets 401 instead.
        if (context.Request.Query.ContainsKey(KaleSignInSetup.SignInErrorParameter))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        // The address to come back to is in the login address the cookie handler gives here, as its ReturnUrl: the
        // page being opened (or the challenge's own RedirectUri). Properties.RedirectUri is not set for [Authorize].
        // It comes from the request, so it is checked: "//evil.example/x" is another host to a browser.
        var loginAddress = new Uri(context.RedirectUri);
        var returnUrl = QueryHelpers.ParseQuery(loginAddress.Query)[context.Options.ReturnUrlParameter].ToString();
        return SignInFlow.SignInAsync(context.HttpContext, SignInRules.IsLocalReturnUrl(returnUrl) ? returnUrl : "/");
    }

    /// <summary>Signed in but not allowed: 403 (ASP.NET Core's default would redirect to an access-denied page this site doesn't have).</summary>
    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    private static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);
}
