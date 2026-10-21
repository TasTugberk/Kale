using System.ComponentModel.DataAnnotations;
using AuthService.Applications;
using AuthService.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Admin;

/// <summary>Admin API: applications. Changes go through <see cref="ApplicationService"/>, like everywhere else.</summary>
public static class ApplicationEndpoints
{
    public sealed record NewApplicationRequest(
        string Key, string Name, IReadOnlyList<Uri>? RedirectUris, IReadOnlyList<Uri>? PostLogoutRedirectUris);

    // Nullable, so a body without it is refused instead of silently meaning false.
    public sealed record ActiveRequest(bool? IsActive);

    public static void MapApplicationEndpoints(this RouteGroupBuilder api)
    {
        var applications = api.MapGroup("/applications");
        applications.MapGet("", ListAsync);
        applications.MapPost("", CreateAsync);
        applications.MapPost("/{id:guid}/secret", RotateSecretAsync);
        applications.MapPut("/{id:guid}/active", SetActiveAsync);
    }

    // A client secret must not stay in a browser's or proxy's cache.
    private static void DoNotStore(HttpContext context) => context.Response.Headers.CacheControl = "no-store";

    private static async Task<IResult> ListAsync(AuthDbContext db, CancellationToken cancellationToken)
    {
        var applications = await db.Applications.AsNoTracking()
            .OrderBy(a => a.Key)
            .Select(a => new { a.Id, a.Key, a.Name, a.IsActive })
            .ToListAsync(cancellationToken);
        return Results.Ok(applications);
    }

    /// <summary>The response is the only time the client secret is shown; only its hash is stored.</summary>
    private static async Task<IResult> CreateAsync(
        NewApplicationRequest request, ApplicationService applications, HttpContext context, CancellationToken cancellationToken)
    {
        var created = await applications.CreateAsync(
            new NewApplication(request.Key, request.Name, request.RedirectUris ?? [], request.PostLogoutRedirectUris ?? []),
            cancellationToken);
        var application = created.Application;
        DoNotStore(context);
        return Results.Json(
            new { application.Id, application.Key, application.Name, application.IsActive, created.ClientSecret },
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> RotateSecretAsync(
        Guid id, AuthDbContext db, ApplicationService applications, HttpContext context, CancellationToken cancellationToken)
    {
        if (!await db.Applications.AnyAsync(a => a.Id == id, cancellationToken))
        {
            return Results.NotFound();
        }

        var secret = await applications.RotateSecretAsync(id, cancellationToken);
        DoNotStore(context);
        return Results.Ok(new { ClientSecret = secret });
    }

    private static async Task<IResult> SetActiveAsync(
        Guid id, ActiveRequest request, AuthDbContext db, ApplicationService applications, CancellationToken cancellationToken)
    {
        if (!await db.Applications.AnyAsync(a => a.Id == id, cancellationToken))
        {
            return Results.NotFound();
        }

        // The service refuses to deactivate "auth" (a 400 through the API's filter).
        var isActive = request.IsActive ?? throw new ValidationException("isActive is required (true or false).");
        await applications.SetActiveAsync(id, isActive, cancellationToken);
        return Results.NoContent();
    }
}
