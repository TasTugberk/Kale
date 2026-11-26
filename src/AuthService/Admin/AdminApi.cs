using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Auth.Client;
using AuthService.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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

        // Like any client app, take invalidation events, so a stopped session or a role that lost auth.Admin is
        // refused right away; in-process, from this instance's own subscribers.
        services.AddHostedService<AdminInvalidationFeed>();
    }

    public static void MapAdminApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/admin/api")
            // Bearer tokens (OpenIddict validation), never the sign-in pages' cookie: a browser signed in to
            // AuthService must not be able to call the admin API on its own (CSRF).
            .RequireAuthorization(new AspNetAuthorizeAttribute { AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme })
            .WithMetadata(new Auth.Client.AuthorizeAttribute(AuthOperations.Admin))
            .AddEndpointFilter(RefusalsAsProblems);

        api.MapGet("/me", GetMeAsync);
        api.MapApplicationEndpoints();
        api.MapRoleEndpoints();
        api.MapUserEndpoints();
        api.MapGroupEndpoints();
        api.MapSessionEndpoints();
    }

    // The services refuse invalid input with a RefusedException; the admin UI gets a 400 with the English text in
    // "detail" and each reason's code and values in "reasons", to show them in the admin's language. An unknown id
    // (also one deleted a moment ago) is a NotFoundException and a 404, so endpoints don't check first.
    private static async ValueTask<object?> RefusalsAsProblems(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (RefusedException exception)
        {
            var reasons = exception.Reasons.Select(reason => new { code = reason.Code, values = reason.Values, detail = reason.Detail }).ToList();
            return Results.Problem(
                detail: exception.Message,
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["reasons"] = reasons });
        }
        catch (ValidationException exception)
        {
            return Results.Problem(detail: exception.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (NotFoundException)
        {
            return Results.NotFound();
        }
        catch (Exception exception) when (IsConcurrentChange(exception))
        {
            // PostgreSQL ended this change because of another one at the same moment, and rolled it back. The lock
            // order (docs/design/locking.md) should prevent it; if it happens anyway, the admin gets "try again"
            // rather than a server error.
            const string detail = "Someone else changed the same data at the same moment. Nothing was saved; try again.";
            var reasons = new[] { new { code = RefusalCodes.ConcurrentChange, values = new Dictionary<string, string>(), detail } };
            return Results.Problem(
                detail: detail,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["reasons"] = reasons });
        }
    }

    // A deadlock (40P01) or a serialization failure (40001), however EF wraps it (DbUpdateException, or
    // InvalidOperationException for a "transient failure").
    // Nothing runs above READ COMMITTED today, so 40001 can't happen yet; it's here so a future
    // REPEATABLE READ or SERIALIZABLE transaction gets the same "try again" answer instead of a 500.
    private static bool IsConcurrentChange(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure })
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<IResult> GetMeAsync(ClaimsPrincipal principal, AuthDbContext db, CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(principal.FindFirstValue(Claims.Subject)!);
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, cancellationToken);
        return Results.Ok(new { user.UserName, user.DisplayName });
    }
}
