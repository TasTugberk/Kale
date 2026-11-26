using System.Security.Claims;
using AuthService.Persistence;
using AuthService.Users;
using Microsoft.EntityFrameworkCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthService.Admin;

/// <summary>
/// Admin API: users. Creating goes through <see cref="UserCreator"/>, (de)activating through
/// <see cref="UserAccounts"/>, a new password through <see cref="UserPasswords"/>, a two-step sign-in reset through
/// <see cref="UserTwoFactor"/>, ending a lockout through <see cref="UserAccounts"/>, access and direct roles
/// through <see cref="UserAccess"/>, correcting names and deleting through <see cref="UserEditor"/>.
/// </summary>
public static class UserEndpoints
{
    public sealed record NewUserRequest(string UserName, string DisplayName, string Password);

    // Nullable, so a body without it is refused instead of silently meaning false.
    public sealed record ActiveRequest(bool? IsActive);

    public sealed record AccessRequest(bool? IsActive, Guid? DefaultRoleId);

    public sealed record PasswordRequest(string? Password);

    public sealed record UserNamesRequest(string? UserName, string? DisplayName);

    public static void MapUserEndpoints(this RouteGroupBuilder api)
    {
        var users = api.MapGroup("/users");
        users.MapGet("", ListAsync);
        users.MapPost("", CreateAsync);
        users.MapPut("/{id:guid}/active", SetActiveAsync);
        users.MapPut("/{id:guid}/password", SetPasswordAsync);
        users.MapDelete("/{id:guid}/two-factor", ResetTwoFactorAsync);
        users.MapDelete("/{id:guid}/lockout", UnlockAsync);
        users.MapGet("/{id:guid}", GetDetailsAsync);
        users.MapPut("/{id:guid}", UpdateAsync);
        users.MapDelete("/{id:guid}", DeleteAsync);
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
    private static async Task<IResult> GetDetailsAsync(Guid id, AuthDbContext db, TimeProvider clock, CancellationToken cancellationToken)
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

        // Identity leaves LockoutEnd in place after it passes: only a lockout still running counts.
        DateTimeOffset? lockedOutUntil = user.LockoutEnd > clock.GetUtcNow() ? user.LockoutEnd : null;

        return Results.Ok(new
        {
            user.Id, user.UserName, user.DisplayName, user.IsActive, user.TwoFactorEnabled, lockedOutUntil, applications, roles, groups,
        });
    }

    /// <summary>A new user name and display name; the user stays signed in.</summary>
    private static async Task<IResult> UpdateAsync(
        Guid id, UserNamesRequest request, UserEditor editor, CancellationToken cancellationToken)
    {
        await editor.UpdateAsync(id, request.UserName ?? "", request.DisplayName ?? "", cancellationToken);
        return Results.NoContent();
    }

    /// <summary>
    /// Deletes the user and everything of theirs; their sessions stop. Refused (a 400) for the last admin and for
    /// the caller's own account.
    /// </summary>
    private static async Task<IResult> DeleteAsync(
        Guid id, ClaimsPrincipal caller, UserEditor editor, CancellationToken cancellationToken)
    {
        var callerId = Guid.Parse(caller.FindFirstValue(Claims.Subject)!);
        await editor.DeleteAsync(id, deletedBy: callerId, cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Deactivating stops the user's sessions in that application.</summary>
    private static async Task<IResult> SetApplicationAccessAsync(
        Guid id, Guid applicationId, AccessRequest request, UserAccess access, CancellationToken cancellationToken)
    {
        await access.SetApplicationAccessAsync(id, applicationId, Required(request.IsActive), request.DefaultRoleId, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> AssignRoleAsync(
        Guid id, Guid roleId, UserAccess access, CancellationToken cancellationToken)
    {
        await access.AssignRoleAsync(id, roleId, cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Sessions using the role stop, unless the user still holds it through a group.</summary>
    private static async Task<IResult> UnassignRoleAsync(
        Guid id, Guid roleId, UserAccess access, CancellationToken cancellationToken)
    {
        await access.UnassignRoleAsync(id, roleId, cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Disabling stops the user's sessions everywhere; the last admin can't be disabled (a 400).</summary>
    private static async Task<IResult> SetActiveAsync(
        Guid id, ActiveRequest request, UserAccounts accounts, CancellationToken cancellationToken)
    {
        if (Required(request.IsActive))
        {
            await accounts.EnableAsync(id, cancellationToken);
        }
        else
        {
            await accounts.DisableAsync(id, cancellationToken);
        }

        return Results.NoContent();
    }

    /// <summary>
    /// An admin's reset: the user's sessions stop everywhere. The answer never holds the password (a 204, or a 400
    /// with Identity's reasons, which don't repeat it; an unknown user is a 404).
    /// </summary>
    private static async Task<IResult> SetPasswordAsync(
        Guid id, PasswordRequest request, UserPasswords passwords, CancellationToken cancellationToken)
    {
        await passwords.SetAsync(id, request.Password ?? "", cancellationToken);
        return Results.NoContent();
    }

    /// <summary>
    /// For a user who lost their authenticator app and recovery codes: it's off (they can set it up
    /// again; with SignIn:RequireTwoFactor, right after their next sign-in), and their sessions stop everywhere.
    /// </summary>
    private static async Task<IResult> ResetTwoFactorAsync(Guid id, UserTwoFactor twoFactor, CancellationToken cancellationToken)
    {
        await twoFactor.ResetAsync(id, cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Ends the user's lockout now (wrong passwords or codes); nothing else changes.</summary>
    private static async Task<IResult> UnlockAsync(Guid id, UserAccounts accounts, CancellationToken cancellationToken)
    {
        await accounts.UnlockAsync(id, cancellationToken);
        return Results.NoContent();
    }

    private static bool Required(bool? isActive) =>
        isActive ?? throw new RefusedException(RefusalCodes.FieldRequired, "isActive is required (true or false).", ("field", "isActive"));
}
