using System.Security.Claims;
using AuthService.Tokens;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthService.SignIn;

/// <summary>
/// connect/authorize, after OpenIddict has validated the request (client, redirect URI, PKCE): our part of
/// the sign-in flow. Not signed in → login page; then the user's access and role decide the session.
/// </summary>
public static class AuthorizeEndpoint
{
    public static void MapAuthorizeEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapMethods("connect/authorize", [HttpMethods.Get, HttpMethods.Post], HandleAsync);

    private static async Task<IResult> HandleAsync(HttpContext context, UserSignIn signIn, CancellationToken cancellationToken)
    {
        var request = context.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("connect/authorize was reached without an OpenID Connect request.");

        var login = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!login.Succeeded)
        {
            // Back to this same authorize request after the login page.
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = context.Request.PathBase + context.Request.Path + context.Request.QueryString },
                [IdentityConstants.ApplicationScheme]);
        }

        var userId = Guid.Parse(login.Principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var outcome = await signIn.StartAsync(userId, request.ClientId!, chosenRoleId: null, cancellationToken);

        return outcome switch
        {
            SignInOutcome.SessionStarted started => Results.SignIn(
                TokenClaims.ForSession(started.Session.UserId, started.Session.Id, started.ApplicationKey, request.GetScopes()),
                authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme),

            // Picking between several roles comes with the role picker page (next branch).
            SignInOutcome.RoleChoiceRequired => Deny("Choosing between several roles is not available yet."),

            SignInOutcome.Denied => Deny("You don't have access to this application."),

            _ => throw new InvalidOperationException($"Unexpected sign-in outcome {outcome}."),
        };
    }

    // Sends the user back to the client app with error=access_denied (the details stay in our logs).
    private static IResult Deny(string description) => Results.Forbid(
        new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.AccessDenied,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }),
        [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
}
