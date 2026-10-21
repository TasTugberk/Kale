using System.Security.Claims;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Tokens;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthService.Grpc;

/// <summary>The application calling AuthService's gRPC API with its client-credentials token.</summary>
public static class AppCaller
{
    public const string Policy = "AppCaller";

    /// <summary>
    /// An app's own token: the subject is the client itself, and it belongs to no user session. Users' tokens for
    /// the "auth" application (the admin UI) pass AuthService's token validation too, for the admin API, so this
    /// check is what keeps them out of the gRPC API.
    /// </summary>
    public static bool IsApplicationToken(ClaimsPrincipal user)
    {
        var clientId = user.GetClaim(Claims.ClientId);
        return clientId is not null
            && user.GetClaim(Claims.Subject) == clientId
            && user.GetClaim(TokenClaims.SessionId) is null;
    }

    /// <summary>
    /// The caller's application, checked on every call: a token issued just before the application was
    /// deactivated must not keep working for its remaining lifetime.
    /// </summary>
    public static async Task<Application> RequireActiveApplicationAsync(this ServerCallContext context, AuthDbContext db)
    {
        var key = context.GetHttpContext().User.GetClaim(Claims.ClientId);
        return await db.Applications.AsNoTracking().SingleOrDefaultAsync(a => a.Key == key && a.IsActive, context.CancellationToken)
            ?? throw new RpcException(new Status(StatusCode.PermissionDenied, "The calling application is inactive."));
    }

    public static Guid ParseId(string value, string field) =>
        Guid.TryParse(value, out var id)
            ? id
            : throw new RpcException(new Status(StatusCode.InvalidArgument, $"{field} must be a UUID."));

    /// <summary>
    /// NOT_FOUND for missing and other applications' items alike, so a caller can't learn what exists elsewhere.
    /// </summary>
    public static RpcException NotFound(string what) => new(new Status(StatusCode.NotFound, $"{what} not found."));
}
