using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Auth.Client;

/// <summary>Satisfied when the caller's session holds any of these operations (qualified names).</summary>
public sealed class OperationRequirement(IReadOnlySet<string> anyOf) : IAuthorizationRequirement
{
    public IReadOnlySet<string> AnyOf { get; } = anyOf;
}

/// <summary>
/// Turns <see cref="AuthorizeAttribute"/>'s policy names into policies; any other name goes to ASP.NET Core's
/// default provider, so the app's own named policies keep working.
/// </summary>
public sealed class OperationPolicyProvider(IOptions<AuthorizationOptions> options, OperationCatalog catalog)
    : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _default = new(options);
    private readonly ConcurrentDictionary<string, AuthorizationPolicy> _policies = new();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(AuthorizeAttribute.PolicyPrefix, StringComparison.Ordinal))
        {
            return _default.GetPolicyAsync(policyName);
        }

        return Task.FromResult<AuthorizationPolicy?>(_policies.GetOrAdd(policyName, Build));
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _default.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _default.GetFallbackPolicyAsync();

    // "kale-operations:BillingOperations:InvoiceRead|PaymentRead" → signed in, and holding billing.InvoiceRead or
    // billing.PaymentRead. The enum's name must be the configured one.
    private AuthorizationPolicy Build(string policyName)
    {
        var enumAndMembers = policyName[AuthorizeAttribute.PolicyPrefix.Length..].Split(':', 2);
        if (enumAndMembers[0] != catalog.EnumType.FullName)
        {
            throw new InvalidOperationException(
                $"[Authorize] uses {enumAndMembers[0]}, but this application's operations are {catalog.EnumType.FullName}.");
        }

        var anyOf = enumAndMembers[1]
            .Split('|')
            .Select(catalog.QualifiedName)
            .ToHashSet(StringComparer.Ordinal);

        return new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new OperationRequirement(anyOf))
            .Build();
    }
}

/// <summary>DESIGN.md per-request check, step 4: evaluates the requirement against the session's operations.</summary>
public sealed partial class OperationAuthorizationHandler(SessionAccess access, ILogger<OperationAuthorizationHandler> logger)
    : AuthorizationHandler<OperationRequirement>
{
    /// <summary>OpenID Connect's session id claim, set by AuthService.</summary>
    public const string SessionIdClaim = "sid";

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, OperationRequirement requirement)
    {
        var sessionId = context.User.FindFirst(SessionIdClaim)?.Value;
        if (sessionId is null)
        {
            return;
        }

        var cancellationToken = (context.Resource as HttpContext)?.RequestAborted ?? CancellationToken.None;
        IReadOnlySet<string>? operations;
        try
        {
            operations = await access.GetOperationsAsync(sessionId, cancellationToken);
        }
        // Only the request's own cancellation passes through; a timeout or gRPC deadline also fails closed.
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Fail closed: without an answer from AuthService nobody gets in, and the request is refused (403)
            // rather than failing with an error page.
            LogLookupFailed(logger, exception);
            return;
        }

        if (operations is not null && requirement.AnyOf.Overlaps(operations))
        {
            context.Succeed(requirement);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't ask AuthService about the session; access refused.")]
    private static partial void LogLookupFailed(ILogger logger, Exception exception);
}

public static class OperationAuthorizationSetup
{
    /// <summary>The authorization part on its own (no token validation or AuthService connection).</summary>
    public static IServiceCollection AddKaleOperationAuthorization(this IServiceCollection services, OperationCatalog catalog)
    {
        services.AddAuthorization();
        services.AddSingleton(catalog);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SessionAccess>();
        services.AddSingleton<IAuthorizationPolicyProvider, OperationPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, OperationAuthorizationHandler>();
        return services;
    }
}
