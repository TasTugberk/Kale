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

    public sealed record ActiveRequest(bool IsActive);

    public static void MapApplicationEndpoints(this RouteGroupBuilder api)
    {
        var applications = api.MapGroup("/applications");
        applications.MapGet("", ListAsync);
        applications.MapPost("", CreateAsync);
        applications.MapPost("/{id:guid}/secret", RotateSecretAsync);
        applications.MapPut("/{id:guid}/active", SetActiveAsync);
    }

    private static async Task<IResult> ListAsync(AuthDbContext db, CancellationToken cancellationToken)
    {
        var applications = await db.Applications.AsNoTracking()
            .OrderBy(a => a.Key)
            .Select(a => new { a.Id, a.Key, a.Name, a.IsActive })
            .ToListAsync(cancellationToken);
        return Results.Ok(applications);
    }

    /// <summary>The response is the only time the client secret is shown; only its hash is stored.</summary>
    private static async Task<IResult> CreateAsync(NewApplicationRequest request, ApplicationService applications, CancellationToken cancellationToken)
    {
        var created = await applications.CreateAsync(
            new NewApplication(request.Key, request.Name, request.RedirectUris ?? [], request.PostLogoutRedirectUris ?? []),
            cancellationToken);
        var application = created.Application;
        return Results.Created(
            $"/admin/api/applications/{application.Id}",
            new { application.Id, application.Key, application.Name, application.IsActive, created.ClientSecret });
    }

    private static async Task<IResult> RotateSecretAsync(
        Guid id, AuthDbContext db, ApplicationService applications, CancellationToken cancellationToken)
    {
        if (!await db.Applications.AnyAsync(a => a.Id == id, cancellationToken))
        {
            return Results.NotFound();
        }

        return Results.Ok(new { ClientSecret = await applications.RotateSecretAsync(id, cancellationToken) });
    }

    private static async Task<IResult> SetActiveAsync(
        Guid id, ActiveRequest request, AuthDbContext db, ApplicationService applications, CancellationToken cancellationToken)
    {
        var key = await db.Applications.Where(a => a.Id == id).Select(a => a.Key).SingleOrDefaultAsync(cancellationToken);
        if (key is null)
        {
            return Results.NotFound();
        }

        // The admin UI signs in to "auth": deactivating it would lock every admin out, with nobody left to undo it.
        if (key == AuthApplication.Key && !request.IsActive)
        {
            throw new ValidationException($"The application '{AuthApplication.Key}' can't be deactivated: the admin UI depends on it.");
        }

        await applications.SetActiveAsync(id, request.IsActive, cancellationToken);
        return Results.NoContent();
    }
}
