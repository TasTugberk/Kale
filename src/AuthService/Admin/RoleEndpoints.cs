using AuthService.Persistence;
using AuthService.Roles;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Admin;

/// <summary>
/// Admin API: an application's operations (registered by the app itself, so read-only here) and its roles.
/// Role changes go through <see cref="RoleEditor"/>, which also tells client apps (events).
/// </summary>
public static class RoleEndpoints
{
    public sealed record RoleRequest(string Name, string? Description);

    public sealed record OperationsRequest(IReadOnlyList<string>? Operations);

    public static void MapRoleEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/applications/{applicationId:guid}/operations", ListOperationsAsync);
        api.MapGet("/applications/{applicationId:guid}/roles", ListRolesAsync);
        api.MapPost("/applications/{applicationId:guid}/roles", CreateRoleAsync);
        api.MapPut("/roles/{roleId:guid}", RenameRoleAsync);
        api.MapPut("/roles/{roleId:guid}/operations", SetOperationsAsync);
        api.MapDelete("/roles/{roleId:guid}", DeleteRoleAsync);
    }

    private static async Task<IResult> ListOperationsAsync(Guid applicationId, AuthDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Applications.AnyAsync(a => a.Id == applicationId, cancellationToken))
        {
            return Results.NotFound();
        }

        var operations = await db.Operations.AsNoTracking()
            .Where(o => o.ApplicationId == applicationId)
            .OrderBy(o => o.Name)
            .Select(o => new { o.Id, o.Name, IsObsolete = o.ObsoletedAt != null })
            .ToListAsync(cancellationToken);
        return Results.Ok(operations);
    }

    /// <summary>Each role with the operations granted to it directly (not the implied ones).</summary>
    private static async Task<IResult> ListRolesAsync(Guid applicationId, AuthDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Applications.AnyAsync(a => a.Id == applicationId, cancellationToken))
        {
            return Results.NotFound();
        }

        var roles = await db.Roles.AsNoTracking()
            .Where(r => r.ApplicationId == applicationId)
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);
        var grants = await db.RoleOperations.AsNoTracking()
            .Where(ro => ro.ApplicationId == applicationId)
            .Join(db.Operations, ro => ro.OperationId, o => o.Id, (ro, o) => new { ro.RoleId, o.Name })
            .ToListAsync(cancellationToken);

        var result = new List<object>();
        foreach (var role in roles)
        {
            var operations = grants.Where(g => g.RoleId == role.Id).Select(g => g.Name).Order(StringComparer.Ordinal).ToList();
            result.Add(new { role.Id, role.Name, role.Description, Operations = operations });
        }

        return Results.Ok(result);
    }

    private static async Task<IResult> CreateRoleAsync(
        Guid applicationId, RoleRequest request, AuthDbContext db, RoleEditor roles, CancellationToken cancellationToken)
    {
        if (!await db.Applications.AnyAsync(a => a.Id == applicationId, cancellationToken))
        {
            return Results.NotFound();
        }

        var role = await roles.CreateAsync(applicationId, request.Name, request.Description, cancellationToken);
        return Results.Json(new { role.Id, role.Name, role.Description }, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> RenameRoleAsync(
        Guid roleId, RoleRequest request, AuthDbContext db, RoleEditor roles, CancellationToken cancellationToken)
    {
        if (!await db.Roles.AnyAsync(r => r.Id == roleId, cancellationToken))
        {
            return Results.NotFound();
        }

        await roles.RenameAsync(roleId, request.Name, request.Description, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> SetOperationsAsync(
        Guid roleId, OperationsRequest request, AuthDbContext db, RoleEditor roles, CancellationToken cancellationToken)
    {
        if (!await db.Roles.AnyAsync(r => r.Id == roleId, cancellationToken))
        {
            return Results.NotFound();
        }

        await roles.SetOperationsAsync(roleId, request.Operations ?? [], cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteRoleAsync(Guid roleId, RoleEditor roles, CancellationToken cancellationToken) =>
        await roles.DeleteAsync(roleId, cancellationToken) ? Results.NoContent() : Results.NotFound();
}
