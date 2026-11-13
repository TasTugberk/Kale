using System.Security.Claims;
using AuthService.Persistence;
using AuthService.Sessions;
using AuthService.SignIn;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthService.Tokens;

/// <summary>
/// connect/token, after OpenIddict has validated the request and authenticated the client (passthrough).
/// Decides what goes into the tokens.
/// </summary>
public static partial class TokenEndpoint
{
    /// <summary>
    /// The audience of tokens apps use to call AuthService itself (registration, session and role lookups).
    /// Deliberately not an application key: keys can't contain ':' (ck_applications_key_format), so a user's
    /// token for any application, including the admin UI's "auth" application, can never carry this audience.
    /// </summary>
    public const string AuthServiceAudience = "urn:kale:auth-api";

    public static void MapTokenEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("connect/token", HandleAsync);

    private static async Task<IResult> HandleAsync(
        HttpContext context, AuthDbContext db, SessionCheck sessionCheck, UserSignIn signIn, IOpenIddictTokenManager tokens,
        TestTokens testTokens, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var request = context.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("connect/token was reached without an OpenID Connect request.");

        if (request.IsClientCredentialsGrantType())
        {
            return await ClientCredentialsAsync(request.ClientId!, db, cancellationToken);
        }

        if (request.IsAuthorizationCodeGrantType())
        {
            var logger = loggerFactory.CreateLogger(typeof(TokenEndpoint));
            return await RedeemCodeAsync(context, request.ClientId!, db, signIn, tokens, logger, cancellationToken);
        }

        if (request.IsRefreshTokenGrantType())
        {
            return await RefreshAsync(context, db, sessionCheck, cancellationToken);
        }

        if (request.GrantType == TestTokens.GrantType)
        {
            return await testTokens.IssueAsync(context, request, cancellationToken);
        }

        return Refuse(Errors.UnsupportedGrantType, "This grant type is not supported.");
    }

    // The code from connect/authorize; OpenIddict has checked it (and PKCE). Decision: the session starts here,
    // not at connect/authorize (see UserSignIn.DecideAsync). The user, their access or their roles may have
    // changed since the code was issued, so StartAsync checks everything again.
    private static async Task<IResult> RedeemCodeAsync(
        HttpContext context, string clientId, AuthDbContext db, UserSignIn signIn, IOpenIddictTokenManager tokens,
        ILogger logger, CancellationToken cancellationToken)
    {
        var principal = await ValidatedGrantAsync(context);

        // Every code carries the role decided at connect/authorize (TokenClaims.ForCode).
        if (!Guid.TryParse(principal.GetClaim(Claims.Subject), out var userId)
            || !Guid.TryParse(principal.GetClaim(TokenClaims.RoleId), out var roleId))
        {
            return Refuse(Errors.InvalidGrant, "The authorization code names no role. Sign in again.");
        }

        // Redeem the code before the session starts. OpenIddict itself marks it redeemed only later, in its sign-in
        // step, so two exchanges of the same code at the same moment (an app that retries, a callback handled
        // twice) would both get here: the second would end the first one's new session, and only one of them would
        // get tokens. TryRedeemAsync updates the code's row under OpenIddict's optimistic concurrency token, so only
        // one exchange wins; OpenIddict's own redeem at sign-in then finds the same row and succeeds again.
        // What stays open: a failure inside OpenIddict's sign-in step after StartAsync has committed leaves a session
        // nobody holds tokens for (it expires with its lifetime) and the earlier session ended, as after any sign-in.
        var code = await tokens.FindByIdAsync(principal.GetTokenId() ?? "", cancellationToken);
        if (code is null || !await tokens.TryRedeemAsync(code, cancellationToken))
        {
            return Refuse(Errors.InvalidGrant, "The authorization code has already been redeemed.");
        }

        var outcome = await signIn.StartAsync(userId, clientId, roleId, cancellationToken);
        switch (outcome)
        {
            case SignInOutcome.SessionStarted started:
                TokenClaims.StartSession(principal, started.Session.Id);
                await SetUserNamesAsync(principal, userId, db, cancellationToken);
                return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

            case SignInOutcome.Denied denied:
                // The reason is for us, not for the app.
                LogRefused(logger, clientId, userId, denied.Reason);
                return Refuse(Errors.InvalidGrant, "The user can no longer sign in to this application.");

            default:
                throw new InvalidOperationException($"Unexpected sign-in outcome {outcome}.");
        }
    }

    // A refresh token from the user's sign-in. OpenIddict has checked it; we check that its session is still
    // ACTIVE, so stopping a session, disabling the user or taking the role away ends refreshing.
    private static async Task<IResult> RefreshAsync(
        HttpContext context, AuthDbContext db, SessionCheck sessionCheck, CancellationToken cancellationToken)
    {
        var principal = await ValidatedGrantAsync(context);

        var session = Guid.TryParse(principal.GetClaim(TokenClaims.SessionId), out var sessionId)
            ? await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
            : null;
        if (session is null || !await sessionCheck.IsActiveAsync(session, cancellationToken))
        {
            return Refuse(Errors.InvalidGrant, "The session is no longer active.");
        }

        // Same claims as at sign-in (sid, user, audience), with the user's current names; OpenIddict issues fresh
        // tokens from them.
        await SetUserNamesAsync(principal, session.UserId, db, cancellationToken);
        return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    // The user's names for the ID token. A lookup by primary key, cheap enough to repeat at every refresh, so a
    // renamed user's next ID token shows the new name.
    private static async Task SetUserNamesAsync(
        ClaimsPrincipal principal, Guid userId, AuthDbContext db, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.DisplayName, u.UserName })
            .SingleAsync(cancellationToken);
        TokenClaims.SetUserNames(principal, user.DisplayName, user.UserName);
    }

    // The code's or refresh token's claims, as OpenIddict validated them.
    private static async Task<ClaimsPrincipal> ValidatedGrantAsync(HttpContext context) =>
        (await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal
            ?? throw new InvalidOperationException("OpenIddict validated the grant but returned no principal.");

    // An app calling AuthService as itself. OpenIddict has already checked its id and secret.
    private static async Task<IResult> ClientCredentialsAsync(string clientId, AuthDbContext db, CancellationToken cancellationToken)
    {
        // applications.is_active is the single source of truth: the client itself stays unchanged.
        var isActive = await db.Applications
            .Where(a => a.Key == clientId)
            .Select(a => a.IsActive)
            .SingleOrDefaultAsync(cancellationToken);
        if (!isActive)
        {
            return Refuse(Errors.InvalidClient, "The application is inactive.");
        }

        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, clientId);
        identity.SetResources(AuthServiceAudience);
        identity.SetDestinations(_ => [Destinations.AccessToken]);

        return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    internal static IResult Refuse(string error, string description) => Results.Forbid(
        new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }),
        [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

    [LoggerMessage(Level = LogLevel.Information, Message = "Code exchange for {Application} refused for user {UserId}: {Reason}")]
    private static partial void LogRefused(ILogger logger, string application, Guid userId, string reason);
}
