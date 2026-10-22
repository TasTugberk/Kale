using AuthService.Admin;
using AuthService.Applications;
using AuthService.SignIn;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthService.Tokens;

/// <summary>
/// Test tokens, Development/Testing only: the admin UI asks connect/token for a user's access token to an
/// application, so developers can try their API without building a sign-in first. The request carries the admin
/// UI's own client credentials, and the admin's access token as the bearer: the same admin check as the admin API.
/// The token is an ordinary 10-minute access token of a real session, started under the normal sign-in rules (so
/// it also ends the user's earlier session in that application). No refresh token, no ID token.
/// </summary>
public sealed partial class TestTokens(
    UserSignIn signIn, IAuthorizationService authorization, IHostEnvironment environment, ILogger<TestTokens> logger)
{
    public const string GrantType = "urn:kale:params:oauth:grant-type:test-token";

    /// <summary>The permission "auth"'s OpenIddict client needs to use the grant type.</summary>
    public const string AdminUiPermission = Permissions.Prefixes.GrantType + GrantType;

    // The admin API's own check (Auth.Client's [Authorize(AuthOperations.Admin)]), so both mean the same "admin".
    private static readonly string AdminPolicy =
        ((IAuthorizeData)new Auth.Client.AuthorizeAttribute(AuthOperations.Admin)).Policy!;

    public async Task<IResult> IssueAsync(HttpContext context, OpenIddictRequest request, CancellationToken cancellationToken)
    {
        // OpenIddict refuses the grant type outside Development/Testing already; checked again so this code never runs there.
        if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
        {
            return TokenEndpoint.Refuse(Errors.UnsupportedGrantType, "Test tokens exist only in Development and Testing.");
        }

        if (request.ClientId != AuthApplication.Key)
        {
            return TokenEndpoint.Refuse(Errors.UnauthorizedClient, "Only the admin UI can ask for test tokens.");
        }

        var admin = await context.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        if (!admin.Succeeded || !(await authorization.AuthorizeAsync(admin.Principal, AdminPolicy)).Succeeded)
        {
            return TokenEndpoint.Refuse(Errors.AccessDenied, "Only an admin (auth.Admin) can ask for test tokens: send the admin's access token as the bearer.");
        }

        var applicationKey = (string?)request.GetParameter("application");
        var roleParameter = (string?)request.GetParameter("role_id");
        if (string.IsNullOrEmpty(applicationKey)
            || !Guid.TryParse((string?)request.GetParameter("user_id"), out var userId)
            || (roleParameter is not null && !Guid.TryParse(roleParameter, out _)))
        {
            return TokenEndpoint.Refuse(Errors.InvalidRequest, "Send application (a key), user_id and optionally role_id (ids).");
        }

        // Not for the admin UI itself: that would be an admin-API token for another admin (acting as them, and
        // ending their admin UI session).
        if (applicationKey == AuthApplication.Key)
        {
            return TokenEndpoint.Refuse(Errors.InvalidRequest, $"No test tokens for '{AuthApplication.Key}', the admin UI itself.");
        }

        Guid? roleId = Guid.TryParse(roleParameter, out var role) ? role : null;

        // A sign-in quietly uses another role when the chosen one isn't held; a test token says so instead.
        if (roleId is { } chosen
            && await signIn.RolesToChooseFromAsync(userId, applicationKey, cancellationToken) is { } eligible
            && eligible.All(r => r.Id != chosen))
        {
            return TokenEndpoint.Refuse(Errors.InvalidRequest, $"The user doesn't hold this role in '{applicationKey}'.");
        }

        var outcome = await signIn.StartAsync(userId, applicationKey, roleId, cancellationToken);
        switch (outcome)
        {
            case SignInOutcome.SessionStarted started:
                var adminId = admin.Principal!.GetClaim(Claims.Subject);
                LogIssued(logger, adminId, userId, applicationKey, started.Session.Id);

                // No scopes: an access token only (no refresh or ID token).
                return Results.SignIn(
                    TokenClaims.ForSession(started.Session.UserId, started.Session.Id, started.ApplicationKey, []),
                    authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

            case SignInOutcome.RoleChoiceRequired:
                return TokenEndpoint.Refuse(Errors.InvalidRequest,
                    $"The user has several roles in '{applicationKey}' and no default one: choose a role (role_id).");

            case SignInOutcome.Denied denied:
                // Shown to the admin: it's their development tool, and says what to fix (e.g. give access first).
                return TokenEndpoint.Refuse(Errors.InvalidGrant, denied.Reason);

            default:
                throw new InvalidOperationException($"Unexpected sign-in outcome {outcome}.");
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Test token issued at the request of admin {AdminId} for user {UserId} in {Application} (session {SessionId}).")]
    private static partial void LogIssued(ILogger logger, string? adminId, Guid userId, string application, Guid sessionId);
}

/// <summary>
/// Development/Testing, at startup: lets the admin UI's client ask for test tokens on a stack set up before they
/// existed. Through ApplicationService, the only code that changes applications' clients.
/// </summary>
public sealed class AdminUiTestTokenAccess(IServiceScopeFactory scopes) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationService>().AllowAdminUiTestTokensAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
