using AuthService.Groups;
using AuthService.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Admin;

/// <summary>
/// Admin API: groups, their members and their roles. Changes go through <see cref="GroupEditor"/>, which stops
/// the sessions that depended on what was taken away.
/// </summary>
public static class GroupEndpoints
{
    public sealed record GroupRequest(string Name, string? Description);

    public static void MapGroupEndpoints(this RouteGroupBuilder api)
    {
        var groups = api.MapGroup("/groups");
        groups.MapGet("", ListAsync);
        groups.MapPost("", CreateAsync);
        groups.MapGet("/{id:guid}", GetDetailsAsync);
        groups.MapPut("/{id:guid}", RenameAsync);
        groups.MapDelete("/{id:guid}", DeleteAsync);
        groups.MapPut("/{id:guid}/members/{userId:guid}", AddMemberAsync);
        groups.MapDelete("/{id:guid}/members/{userId:guid}", RemoveMemberAsync);
        groups.MapPut("/{id:guid}/roles/{roleId:guid}", AddRoleAsync);
        groups.MapDelete("/{id:guid}/roles/{roleId:guid}", RemoveRoleAsync);
    }

    private static async Task<IResult> ListAsync(AuthDbContext db, CancellationToken cancellationToken)
    {
        var groups = await db.Groups.AsNoTracking()
            .OrderBy(g => g.Name)
            .Select(g => new { g.Id, g.Name, g.Description })
            .ToListAsync(cancellationToken);
        return Results.Ok(groups);
    }

    private static async Task<IResult> CreateAsync(GroupRequest request, GroupEditor groups, CancellationToken cancellationToken)
    {
        var group = await groups.CreateAsync(request.Name, request.Description, cancellationToken);
        return Results.Json(new { group.Id, group.Name, group.Description }, statusCode: StatusCodes.Status201Created);
    }

    /// <summary>The group with its members and its roles (each with its application).</summary>
    private static async Task<IResult> GetDetailsAsync(Guid id, AuthDbContext db, CancellationToken cancellationToken)
    {
        var group = await db.Groups.AsNoTracking().SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (group is null)
        {
            return Results.NotFound();
        }

        var members = await db.UserGroups.AsNoTracking()
            .Where(ug => ug.GroupId == id)
            .Join(db.Users, ug => ug.UserId, user => user.Id, (_, user) => new { UserId = user.Id, user.UserName, user.DisplayName })
            .OrderBy(m => m.UserName)
            .ToListAsync(cancellationToken);
        var roles = await db.GroupRoles.AsNoTracking()
            .Where(gr => gr.GroupId == id)
            .Join(db.Roles, gr => gr.RoleId, role => role.Id, (_, role) => role)
            .Join(db.Applications, role => role.ApplicationId, application => application.Id,
                (role, application) => new { RoleId = role.Id, role.Name, ApplicationKey = application.Key })
            .OrderBy(r => r.ApplicationKey).ThenBy(r => r.Name)
            .ToListAsync(cancellationToken);

        return Results.Ok(new { group.Id, group.Name, group.Description, members, roles });
    }

    private static async Task<IResult> RenameAsync(
        Guid id, GroupRequest request, AuthDbContext db, GroupEditor groups, CancellationToken cancellationToken)
    {
        if (!await db.Groups.AnyAsync(g => g.Id == id, cancellationToken))
        {
            return Results.NotFound();
        }

        await groups.RenameAsync(id, request.Name, request.Description, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(Guid id, GroupEditor groups, CancellationToken cancellationToken) =>
        await groups.DeleteAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound();

    private static async Task<IResult> AddMemberAsync(
        Guid id, Guid userId, AuthDbContext db, GroupEditor groups, CancellationToken cancellationToken)
    {
        if (!await db.Groups.AnyAsync(g => g.Id == id, cancellationToken) || !await db.Users.AnyAsync(u => u.Id == userId, cancellationToken))
        {
            return Results.NotFound();
        }

        await groups.AddMemberAsync(id, userId, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveMemberAsync(
        Guid id, Guid userId, AuthDbContext db, GroupEditor groups, CancellationToken cancellationToken)
    {
        if (!await db.Groups.AnyAsync(g => g.Id == id, cancellationToken) || !await db.Users.AnyAsync(u => u.Id == userId, cancellationToken))
        {
            return Results.NotFound();
        }

        await groups.RemoveMemberAsync(id, userId, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> AddRoleAsync(
        Guid id, Guid roleId, AuthDbContext db, GroupEditor groups, CancellationToken cancellationToken)
    {
        if (!await db.Groups.AnyAsync(g => g.Id == id, cancellationToken) || !await db.Roles.AnyAsync(r => r.Id == roleId, cancellationToken))
        {
            return Results.NotFound();
        }

        await groups.AddRoleAsync(id, roleId, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveRoleAsync(
        Guid id, Guid roleId, AuthDbContext db, GroupEditor groups, CancellationToken cancellationToken)
    {
        if (!await db.Groups.AnyAsync(g => g.Id == id, cancellationToken) || !await db.Roles.AnyAsync(r => r.Id == roleId, cancellationToken))
        {
            return Results.NotFound();
        }

        await groups.RemoveRoleAsync(id, roleId, cancellationToken);
        return Results.NoContent();
    }
}
