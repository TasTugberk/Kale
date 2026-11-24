using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auth.Client;

/// <summary>Satisfied when the caller's Kale session is ACTIVE, whatever operations its role holds.</summary>
public sealed class KaleSessionRequirement : IAuthorizationRequirement;

/// <summary>
/// Checks <see cref="KaleSessionRequirement"/> like <see cref="OperationAuthorizationHandler"/> checks an operation:
/// same lookups and caches, and the same refusals (401 for an ended session, 503 when AuthService can't be reached).
/// </summary>
public sealed class KaleSessionHandler(SessionAccess access, ILogger<KaleSessionHandler> logger)
    : AuthorizationHandler<KaleSessionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, KaleSessionRequirement requirement)
    {
        var operations = await SessionOperations.GetOrRefuseAsync(context, this, access, logger);
        if (operations is not null)
        {
            context.Succeed(requirement);
        }
    }
}

public static class KaleSessionPolicy
{
    /// <summary>
    /// Adds the Kale session check to a policy of the application's own, e.g. its own default policy:
    /// <c>new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireKaleSession().Build()</c>.
    /// </summary>
    public static AuthorizationPolicyBuilder RequireKaleSession(this AuthorizationPolicyBuilder policy) =>
        policy.AddRequirements(new KaleSessionRequirement());

    /// <summary>
    /// Plain [Authorize] and RequireAuthorization() use the default policy, which on its own only asks for a valid
    /// token: a stopped session's token would keep working there until it expires. This makes the default "signed in,
    /// and the Kale session is active". Only ASP.NET Core's built-in default is replaced: a default policy the
    /// application set itself stays (add <see cref="RequireKaleSession"/> to it to keep the check). PostConfigure runs
    /// after every Configure, so the order of the calls doesn't matter.
    /// </summary>
    public static IServiceCollection AddKaleSessionToDefaultPolicy(this IServiceCollection services)
    {
        services.PostConfigure<AuthorizationOptions>(options =>
        {
            if (IsBuiltInDefault(options.DefaultPolicy))
            {
                options.DefaultPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireKaleSession().Build();
            }
        });
        return services;
    }

    // What AuthorizationOptions starts with: any authenticated user, through the default scheme.
    private static bool IsBuiltInDefault(AuthorizationPolicy policy) =>
        policy.AuthenticationSchemes.Count == 0 && policy.Requirements is [DenyAnonymousAuthorizationRequirement];
}
