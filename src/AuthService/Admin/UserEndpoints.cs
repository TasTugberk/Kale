using AuthService.Persistence;
using AuthService.Users;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Admin;

/// <summary>
/// Admin API: users. Creating goes through <see cref="UserCreator"/>, (de)activating through
/// <see cref="UserAccounts"/>, access and direct roles through <see cref="UserAccess"/>.
/// </summary>
public static class UserEndpoints
{
    public sealed record NewUserRequest(string UserName, string DisplayName, string Password);

    public sealed record ActiveRequest(bool IsActive);

    public sealed record AccessRequest(bool IsActive, Guid? DefaultRoleId);

    public static void MapUserEndpoints(this RouteGroupBuilder api)
    {
        var users = api.MapGroup("/users");
        users.MapGet("", ListAsync);
        users.MapPost("", CreateAsync);
        users.MapPut("/{id:guid}/active", SetActiveAsync);
        users.MapGet("/{id:guid}", GetDetailsAsync);
        users.MapPut("/{id:guid}/applications/{applicationId:guid}", SetApplicationAccessAsync);
        users.MapPut("/{id:guid}/roles/{roleId:guid}", AssignRoleAsync);
        users.MapDelete("/{id:guid}/roles/{roleId:guid}", UnassignRoleAsync);
    }

    private static async Task<IResult> ListAsync(AuthDbContext db, CancellationToken cancellationToken)
    {
        var users = await db.Users.AsNoTracking()
            .OrderBy(u => u.UserName)
            .Select(u => new { u.Id, u.UserName, u.DisplayName, u.IsActive })
            .ToListAsync(cancellationToken);
        return Results.Ok(users);
    }

    private static async Task<IResult> CreateAsync(NewUserRequest request, UserCreator creator)
    {
        var user = await creator.CreateAsync(request.UserName, request.DisplayName, request.Password);
        return Results.Json(new { user.Id, user.UserName, user.DisplayName, user.IsActive }, statusCode: StatusCodes.Status201Created);
    }

    /// <summary>The user with their access to applications, their direct roles and their groups.</summary>
    private static async Task<IResult> GetDetailsAsync(Guid id, AuthDbContext db, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null)
        {
            return Results.NotFound();
        }

        var applications = await db.UserApplications.AsNoTracking()
            .Where(a => a.UserId == id)
            .Join(db.Applications, a => a.ApplicationId, application => application.Id,
                (a, application) => new { a.ApplicationId, ApplicationKey = application.Key, a.IsActive, a.DefaultRoleId })
            .OrderBy(a => a.ApplicationKey)
            .ToListAsync(cancellationToken);
        var roles = await db.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == id)
            .Join(db.Roles, ur => ur.RoleId, role => role.Id, (_, role) => role)
            .Join(db.Applications, role => role.ApplicationId, application => application.Id,
                (role, application) => new { RoleId = role.Id, role.Name, ApplicationKey = application.Key })
            .OrderBy(r => r.ApplicationKey).ThenBy(r => r.Name)
            .ToListAsync(cancellationToken);
        var groups = await db.UserGroups.AsNoTracking()
            .Where(ug => ug.UserId == id)
            .Join(db.Groups, ug => ug.GroupId, group => group.Id, (_, group) => new { GroupId = group.Id, group.Name })
            .OrderBy(g => g.Name)
            .ToListAsync(cancellationToken);

        return Results.Ok(new { user.Id, user.UserName, user.DisplayName, user.IsActive, applications, roles, groups });
    }

    /// <summary>Deactivating stops the user's sessions in that application.</summary>
    private static async Task<IResult> SetApplicationAccessAsync(
        Guid id, Guid applicationId, AccessRequest request, AuthDbContext db, UserAccess access, CancellationToken cancellationToken)
    {
        if (!await db.Users.AnyAsync(u => u.Id == id, cancellationToken)
            || !await db.Applications.AnyAsync(a => a.Id == applicationId, cancellationToken))
        {
            return Results.NotFound();
        }

        await access.SetApplicationAccessAsync(id, applicationId, request.IsActive, request.DefaultRoleId, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> AssignRoleAsync(
        Guid id, Guid roleId, AuthDbContext db, UserAccess access, CancellationToken cancellationToken)
    {
        if (!await db.Users.AnyAsync(u => u.Id == id, cancellationToken) || !await db.Roles.AnyAsync(r => r.Id == roleId, cancellationToken))
        {
            return Results.NotFound();
        }

        await access.AssignRoleAsync(id, roleId, cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Sessions using the role stop, unless the user still holds it through a group.</summary>
    private static async Task<IResult> UnassignRoleAsync(
        Guid id, Guid roleId, AuthDbContext db, UserAccess access, CancellationToken cancellationToken)
    {
        if (!await db.Users.AnyAsync(u => u.Id == id, cancellationToken))
        {
            return Results.NotFound();
        }

        await access.UnassignRoleAsync(id, roleId, cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Disabling stops the user's sessions everywhere; the last admin can't be disabled (a 400).</summary>
    private static async Task<IResult> SetActiveAsync(
        Guid id, ActiveRequest request, AuthDbContext db, UserAccounts accounts, CancellationToken cancellationToken)
    {
        if (!await db.Users.AnyAsync(u => u.Id == id, cancellationToken))
        {
            return Results.NotFound();
        }

        if (request.IsActive)
        {
            await accounts.EnableAsync(id, cancellationToken);
        }
        else
        {
            await accounts.DisableAsync(id, cancellationToken);
        }

        return Results.NoContent();
    }
}
