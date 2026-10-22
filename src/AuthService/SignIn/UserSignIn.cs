using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Roles;
using AuthService.Sessions;
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
/// else the only role) and create the session, ending the user's earlier session in that application. A user
/// signed in to two applications has two sessions.
/// </summary>
public sealed class UserSignIn(
    AuthDbContext db, EligibleRoles eligibleRoles, SessionSettings sessionSettings, SessionStopper sessionStopper, TimeProvider clock)
{
    public async Task<SignInOutcome> StartAsync(
        Guid userId, string applicationKey, Guid? chosenRoleId, CancellationToken cancellationToken = default)
    {
        // Shared lock on the user until the session is saved: UserAccounts and UserAccess lock the same row, so a
        // change to the user either waits for this sign-in (and then stops its session) or this waits and sees it.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM users WHERE id = {userId} FOR SHARE", cancellationToken);

        var access = await FindActiveAccessAsync(userId, applicationKey, cancellationToken);
        if (access is null)
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

        await EndEarlierSessionsAsync(userId, access.ApplicationId, cancellationToken);
        var session = new Session
        {
            UserId = userId,
            ApplicationId = access.ApplicationId,
            RoleId = roleId.Value,
            ExpiresAt = clock.GetUtcNow() + sessionSettings.Lifetime,
        };
        db.Sessions.Add(session);

        // One save: the new session, the ended ones and their "session stopped" events, so client apps refuse
        // the earlier tokens right away.
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SignInOutcome.SessionStarted(session, applicationKey);
    }

    /// <summary>
    /// The roles the role picker may show: only for a user with active access to an active application (the
    /// same rule as signing in), otherwise null. Null for unknown applications too, so the picker can't be used
    /// to find out which application keys exist.
    /// </summary>
    public async Task<IReadOnlyList<EligibleRole>?> RolesToChooseFromAsync(
        Guid userId, string applicationKey, CancellationToken cancellationToken = default)
    {
        var access = await FindActiveAccessAsync(userId, applicationKey, cancellationToken);
        return access is null ? null : await eligibleRoles.ForUserAsync(userId, access.ApplicationId, cancellationToken);
    }

    /// <summary>
    /// Decision: one open session per user and application. A new sign-in (another device, or a role switch)
    /// ends the earlier one, so its tokens stop working. Sessions in other applications stay.
    /// </summary>
    private async Task EndEarlierSessionsAsync(Guid userId, Guid applicationId, CancellationToken cancellationToken)
    {
        // The access row is locked until this sign-in commits, so two sign-ins of the same user to the same
        // application take turns: the later one sees the earlier one's session and ends it. (Same lock order as
        // UserAccess: the user first, then the access row.)
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM user_applications WHERE user_id = {userId} AND application_id = {applicationId} FOR UPDATE",
            cancellationToken);

        // Only open ones: an ended or expired session has nothing left to stop.
        var now = clock.GetUtcNow();
        var earlier = await db.Sessions
            .Where(s => s.UserId == userId && s.ApplicationId == applicationId && s.EndedAt == null && s.ExpiresAt > now)
            .ToListAsync(cancellationToken);
        foreach (var session in earlier)
        {
            sessionStopper.Stop(session);
        }
    }

    private sealed record ActiveAccess(Guid ApplicationId, Guid? DefaultRoleId);

    // Sign-in requires an active application, an active user and an active access row (decision: roles alone
    // are not enough).
    private Task<ActiveAccess?> FindActiveAccessAsync(Guid userId, string applicationKey, CancellationToken cancellationToken) =>
        (from application in db.Applications
         join userApplication in db.UserApplications on application.Id equals userApplication.ApplicationId
         join user in db.Users on userApplication.UserId equals user.Id
         where application.Key == applicationKey
             && user.Id == userId
             && application.IsActive
             && userApplication.IsActive
             && user.IsActive
         select new ActiveAccess(application.Id, userApplication.DefaultRoleId))
        .SingleOrDefaultAsync(cancellationToken);

    private static Guid? Pick(Guid? roleId, IReadOnlyList<EligibleRole> roles) =>
        roleId is { } id && roles.Any(r => r.Id == id) ? id : null;
}

/// <param name="Lifetime">Absolute session length; refresh tokens last exactly as long (decision: 8 h).</param>
public sealed record SessionSettings(TimeSpan Lifetime);
