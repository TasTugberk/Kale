using System.Security.Claims;
using AuthService.Tokens;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthService.SignIn;

/// <summary>
/// connect/authorize, after OpenIddict has validated the request (client, redirect URI, PKCE): our part of
/// the sign-in flow. Not signed in (or the app wants a fresh login) → login page; then the user's access and
/// role decide the session.
/// </summary>
public static partial class AuthorizeEndpoint
{
    public static void MapAuthorizeEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapMethods("connect/authorize", [HttpMethods.Get, HttpMethods.Post], HandleAsync);

    private static async Task<IResult> HandleAsync(
        HttpContext context, UserSignIn signIn, TimeProvider clock, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var request = context.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("connect/authorize was reached without an OpenID Connect request.");

        var login = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!login.Succeeded || MustLogInAgain(request, login, clock))
        {
            // prompt=none: the app asked us not to show any page (e.g. a silent check in the background).
            if (request.HasPromptValue(PromptValues.None))
            {
                return Deny(Errors.LoginRequired, "The user is not signed in.");
            }

            // Back to this same request after the login page; without prompt, so prompt=login asks only once.
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = ReturnUrl(context.Request, request, Parameters.Prompt) },
                [IdentityConstants.ApplicationScheme]);
        }

        var userId = Guid.Parse(login.Principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var outcome = await signIn.StartAsync(userId, request.ClientId!, chosenRoleId: null, cancellationToken);

        switch (outcome)
        {
            case SignInOutcome.SessionStarted started:
                return Results.SignIn(
                    TokenClaims.ForSession(started.Session.UserId, started.Session.Id, started.ApplicationKey, request.GetScopes()),
                    authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

            // Picking between several roles comes with the role picker page (next branch).
            case SignInOutcome.RoleChoiceRequired:
                return Deny(Errors.AccessDenied, "Choosing between several roles is not available yet.");

            case SignInOutcome.Denied denied:
                // The reason is for us, not for the user or the app.
                var logger = loggerFactory.CreateLogger(typeof(AuthorizeEndpoint));
                LogDenied(logger, request.ClientId!, userId, denied.Reason);
                return Deny(Errors.AccessDenied, "You don't have access to this application.");

            default:
                throw new InvalidOperationException($"Unexpected sign-in outcome {outcome}.");
        }
    }

    // prompt=login, or a login older than max_age seconds: the app wants the password entered again.
    private static bool MustLogInAgain(OpenIddictRequest request, AuthenticateResult login, TimeProvider clock) =>
        request.HasPromptValue(PromptValues.Login)
        || (request.MaxAge is { } maxAge
            && login.Properties?.IssuedUtc is { } loggedInAt
            && clock.GetUtcNow() - loggedInAt > TimeSpan.FromSeconds(maxAge));

    // The same authorize request as a GET URL, rebuilt from OpenIddict's parsed parameters so that a form post
    // survives the login page too. Leaves out the given parameters.
    private static string ReturnUrl(HttpRequest httpRequest, OpenIddictRequest request, params string[] leaveOut)
    {
        var parameters = request.GetParameters()
            .Where(parameter => !leaveOut.Contains(parameter.Key))
            .Select(parameter => KeyValuePair.Create(parameter.Key, (string?)parameter.Value));
        return httpRequest.PathBase + httpRequest.Path + QueryString.Create(parameters);
    }

    // Sends the user back to the client app with this OAuth error.
    private static IResult Deny(string error, string description) => Results.Forbid(
        new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }),
        [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sign-in to {Application} refused for user {UserId}: {Reason}")]
    private static partial void LogDenied(ILogger logger, string application, Guid userId, string reason);
}
