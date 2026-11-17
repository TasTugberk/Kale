using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Auth.Web;

public static class KaleSignInEndpoints
{
    /// <summary>
    /// The endpoints a front end (e.g. React) uses, under <paramref name="prefix"/>:
    /// <list type="bullet">
    /// <item><c>GET {prefix}/login?returnUrl=/path</c>: open it (a link, <c>location.href</c>) to sign in; back to
    /// returnUrl (a path on this site) afterwards.</item>
    /// <item><c>POST {prefix}/logout?returnUrl=/path</c>: a form on this site's pages (other origins get 403); signs out
    /// here and at Kale.</item>
    /// <item><c>GET {prefix}/me</c>: the signed-in user (sub, name and preferred_username), or 401.</item>
    /// </list>
    /// Sign-in errors (e.g. no access to this application) come back to returnUrl as <c>?kaleError=access_denied</c>
    /// (a page that needs sign-in then answers 401 instead of asking Kale again).
    /// </summary>
    public static RouteGroupBuilder MapKaleSignIn(this IEndpointRouteBuilder endpoints, string prefix = "/auth")
    {
        var group = endpoints.MapGroup(prefix);
        group.MapGet("/login", LogInAsync);
        group.MapPost("/logout", LogOutAsync);
        // With its return type spelled out: MeAsync(HttpContext) on its own would bind as a RequestDelegate (HttpContext
        // in, Task out), which drops the result it returns.
        group.MapGet("/me", Task<IResult> (HttpContext context) => MeAsync(context));
        return group;
    }

    private static async Task<IResult> LogInAsync(HttpContext context, string? returnUrl)
    {
        returnUrl ??= "/";
        if (!SignInRules.IsLocalReturnUrl(returnUrl))
        {
            return Results.BadRequest("returnUrl must be a path on this site, e.g. /orders.");
        }

        var signedIn = await context.AuthenticateAsync(KaleSignInSetup.CookieScheme);
        if (signedIn.Succeeded)
        {
            return Results.Redirect(returnUrl);
        }

        await SignInFlow.SignInAsync(context, returnUrl);
        return Results.Empty;
    }

    private static async Task<IResult> LogOutAsync(HttpContext context, string? returnUrl)
    {
        // Another site must not be able to sign the user out (a form posted from elsewhere).
        if (!SignInRules.IsSameOrigin(context.Request))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        returnUrl ??= "/";
        if (!SignInRules.IsLocalReturnUrl(returnUrl))
        {
            return Results.BadRequest("returnUrl must be a path on this site, e.g. /.");
        }

        await SignInFlow.SignOutEverywhereAsync(context, await SignInFlow.IdTokenAsync(context), returnUrl);
        return Results.Empty;
    }

    private static async Task<IResult> MeAsync(HttpContext context)
    {
        var signedIn = await context.AuthenticateAsync(KaleSignInSetup.CookieScheme);
        if (!signedIn.Succeeded)
        {
            return Results.Unauthorized();
        }

        var user = signedIn.Principal;
        return Results.Ok(new SignedInUser(
            user.FindFirst("sub")?.Value,
            user.FindFirst("name")?.Value,
            user.FindFirst("preferred_username")?.Value));
    }

    // Named as the ID token's claims: the user's id, display name and user name, as of the sign-in. No "sid": the
    // page's script needs nothing to do with the Kale session.
    private sealed record SignedInUser(
        [property: JsonPropertyName("sub")] string? Sub,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("preferred_username")] string? PreferredUsername);
}
