using System.Security.Claims;
using Auth.Client;
using AuthService.Outbox;
using AuthService.Persistence;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;
using AspNetAuthorizeAttribute = Microsoft.AspNetCore.Authorization.AuthorizeAttribute;

namespace AuthService.Admin;

/// <summary>
/// The REST API behind the admin UI (/admin/api). Every endpoint needs a user's access token for the "auth"
/// application whose session holds auth.Admin: Auth.Client's own [Authorize(Operation)], answered in-process.
/// </summary>
public static class AdminApi
{
    public static void AddAdminApi(this IServiceCollection services)
    {
        services.AddKaleOperationAuthorization(OperationCatalog.For<AuthOperations>(AuthApplication.Key));
        services.AddSingleton<AdminSessionLookups>();
        services.AddSingleton<ISessionLookup>(provider => provider.GetRequiredService<AdminSessionLookups>());
        services.AddSingleton<IRoleOperationsLookup>(provider => provider.GetRequiredService<AdminSessionLookups>());

        // Like any client app, listen for invalidation events, so a stopped session or a role that lost
        // auth.Admin is refused right away. It uses AuthService's own RabbitMQ settings (none: caches expire by
        // time only, which only Development/Testing allow).
        services.AddSingleton(provider => new KaleAuthOptions
        {
            ApplicationKey = AuthApplication.Key,
            RabbitMqUri = provider.GetService<RabbitMqSettings>()?.Uri,
            RabbitMqPassword = provider.GetService<RabbitMqSettings>()?.Password,
        });
        services.AddHostedService<InvalidationListener>();
    }

    public static void MapAdminApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/admin/api")
            // Bearer tokens (OpenIddict validation), never the sign-in pages' cookie: a browser signed in to
            // AuthService must not be able to call the admin API on its own (CSRF).
            .RequireAuthorization(new AspNetAuthorizeAttribute { AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme })
            .WithMetadata(new Auth.Client.AuthorizeAttribute(AuthOperations.Admin));

        api.MapGet("/me", GetMeAsync);
    }

    private static async Task<IResult> GetMeAsync(ClaimsPrincipal principal, AuthDbContext db, CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(principal.FindFirstValue(Claims.Subject)!);
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, cancellationToken);
        return Results.Ok(new { user.UserName, user.DisplayName });
    }
}
