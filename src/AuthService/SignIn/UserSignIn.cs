using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Roles;
using Microsoft.EntityFrameworkCore;

namespace AuthService.SignIn;

/// <summary>The outcome of trying to sign a user in to an application.</summary>
public abstract record SignInOutcome
{
    /// <summary>A session was created; its id goes into the tokens as "sid".</summary>
    public sealed record SessionStarted(Session Session, string ApplicationKey) : SignInOutcome;

    /// <summary>The user has several eligible roles and no usable default: they must pick one.</summary>
    public sealed record RoleChoiceRequired(IReadOnlyList<EligibleRole> Roles) : SignInOutcome;

    /// <summary>Not allowed (inactive application, user or access, or no role). The reason is for logs, not users.</summary>
    public sealed record Denied(string Reason) : SignInOutcome;
}

/// <summary>
/// DESIGN.md sign-in steps 3-4: decide the role (the user's choice, else the default role if it's eligible,
/// else the only role) and create the session. A user signed in to two applications has two sessions.
/// </summary>
public sealed class UserSignIn(AuthDbContext db, EligibleRoles eligibleRoles, SessionSettings sessionSettings, TimeProvider clock)
{
    public async Task<SignInOutcome> StartAsync(
        Guid userId, string applicationKey, Guid? chosenRoleId, CancellationToken cancellationToken = default)
    {
        var access = await (
                from application in db.Applications
                join userApplication in db.UserApplications on application.Id equals userApplication.ApplicationId
                join user in db.Users on userApplication.UserId equals user.Id
                where application.Key == applicationKey && user.Id == userId
                select new
                {
                    ApplicationId = application.Id,
                    ApplicationActive = application.IsActive,
                    AccessActive = userApplication.IsActive,
                    UserActive = user.IsActive,
                    userApplication.DefaultRoleId,
                })
            .SingleOrDefaultAsync(cancellationToken);

        // Sign-in requires an active access row (decision: roles alone are not enough).
        if (access is null || !access.ApplicationActive || !access.AccessActive || !access.UserActive)
        {
            return new SignInOutcome.Denied("No active access to this application.");
        }

        var roles = await eligibleRoles.ForUserAsync(userId, access.ApplicationId, cancellationToken);
        if (roles.Count == 0)
        {
            return new SignInOutcome.Denied("No role in this application.");
        }

        // A chosen or default role only counts if it's one the user actually holds right now.
        var roleId = Pick(chosenRoleId, roles) ?? Pick(access.DefaultRoleId, roles) ?? (roles.Count == 1 ? roles[0].Id : null);
        if (roleId is null)
        {
            return new SignInOutcome.RoleChoiceRequired(roles);
        }

        var session = new Session
        {
            UserId = userId,
            ApplicationId = access.ApplicationId,
            RoleId = roleId.Value,
            ExpiresAt = clock.GetUtcNow() + sessionSettings.Lifetime,
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return new SignInOutcome.SessionStarted(session, applicationKey);
    }

    private static Guid? Pick(Guid? roleId, IReadOnlyList<EligibleRole> roles) =>
        roleId is { } id && roles.Any(r => r.Id == id) ? id : null;
}

/// <param name="Lifetime">Absolute session length; refresh tokens last exactly as long (decision: 8 h).</param>
public sealed record SessionSettings(TimeSpan Lifetime);
