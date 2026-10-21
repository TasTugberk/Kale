using AuthService.Persistence;
using AuthService.Users;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Admin;

/// <summary>Admin API: users. Creating goes through <see cref="UserCreator"/>, (de)activating through <see cref="UserAccounts"/>.</summary>
public static class UserEndpoints
{
    public sealed record NewUserRequest(string UserName, string DisplayName, string Password);

    public sealed record ActiveRequest(bool IsActive);

    public static void MapUserEndpoints(this RouteGroupBuilder api)
    {
        var users = api.MapGroup("/users");
        users.MapGet("", ListAsync);
        users.MapPost("", CreateAsync);
        users.MapPut("/{id:guid}/active", SetActiveAsync);
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
