using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
public sealed class OperationAuthorizationHandler(SessionAccess access, ILogger<OperationAuthorizationHandler> logger)
    : AuthorizationHandler<OperationRequirement>
{
    /// <summary>OpenID Connect's session id claim, set by AuthService.</summary>
    public const string SessionIdClaim = "sid";

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, OperationRequirement requirement)
    {
        var operations = await SessionOperations.GetOrRefuseAsync(context, this, access, logger);
        if (operations is not null && requirement.AnyOf.Overlaps(operations))
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>
/// DESIGN.md per-request check, steps 2-3, for both Kale handlers (operations, and the plain session check): the
/// session's operations, or null when the request must be refused, with the reason recorded for
/// <see cref="KaleAuthorizationResultHandler"/>.
/// </summary>
internal static partial class SessionOperations
{
    public static async Task<IReadOnlySet<string>?> GetOrRefuseAsync(
        AuthorizationHandlerContext context, IAuthorizationHandler handler, SessionAccess access, ILogger logger)
    {
        var sessionId = context.User.FindFirst(OperationAuthorizationHandler.SessionIdClaim)?.Value;
        if (sessionId is null)
        {
            return null;
        }

        var cancellationToken = (context.Resource as HttpContext)?.RequestAborted ?? CancellationToken.None;
        IReadOnlySet<string>? operations;
        try
        {
            operations = await access.GetOperationsAsync(sessionId, cancellationToken);
        }
        // Only the request's own cancellation passes through; a timeout or gRPC deadline also fails closed.
        catch (AuthServiceUnreachableException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Fail closed, with no usable cached answer left: 503 (KaleAuthorizationResultHandler), so the caller
            // tries again later instead of telling the user they aren't allowed.
            LogAuthServiceUnreachable(logger, exception);
            context.Fail(new AuthServiceUnreachableReason(handler));
            return null;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Fail closed: AuthService answered with an error (e.g. it refused this application's own token), so
            // the request is refused (403) rather than failing with an error page.
            LogLookupFailed(logger, exception);
            return null;
        }

        if (operations is null)
        {
            // Ended, not ACTIVE any more, unknown, or its role is gone: 401, the user has to sign in again.
            context.Fail(new SessionNotActiveReason(handler));
        }

        return operations;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't ask AuthService about the session; access refused.")]
    private static partial void LogLookupFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "AuthService couldn't be reached to check the session; access refused (503).")]
    private static partial void LogAuthServiceUnreachable(ILogger logger, Exception exception);
}

/// <summary>Why a Kale handler refused: the token's Kale session is over (401).</summary>
public sealed class SessionNotActiveReason(IAuthorizationHandler handler)
    : AuthorizationFailureReason(handler, "The Kale session has ended or isn't active any more; sign in again.");

/// <summary>Why a Kale handler refused: AuthService couldn't be asked, and no cached answer could stand in (503).</summary>
public sealed class AuthServiceUnreachableReason(IAuthorizationHandler handler)
    : AuthorizationFailureReason(handler, "AuthService couldn't be reached to check the session.");

/// <summary>
/// Answers a refused [Authorize(Operation)] (or plain [Authorize], see KaleSessionPolicy) with a status code the caller can act on: 401 when the Kale session is
/// over (sign in again), 503 when AuthService couldn't be reached (try again later), 403 when the role lacks the
/// operation. Everything else (no token, the app's own policies) is ASP.NET Core's default. An application that needs
/// its own result handler derives it from this one (startup fails otherwise: see ResultHandlerCheck).
/// </summary>
public class KaleAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716", Justification = "Named as in IAuthorizationMiddlewareResultHandler.HandleAsync.")]
    public virtual async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        var reasons = authorizeResult.AuthorizationFailure?.FailureReasons ?? [];
        if (authorizeResult.Forbidden && reasons.Any(reason => reason is SessionNotActiveReason))
        {
            // Checked first: an ended session is a sure answer.
            await ChallengeAsync(context, policy);
            return;
        }

        if (authorizeResult.Forbidden && reasons.Any(reason => reason is AuthServiceUnreachableReason))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }

    // A challenge, as for a missing token (and to the same schemes as ASP.NET Core's default handler): the bearer
    // scheme answers 401, and Kale.Auth.Web's cookie sends a page to sign in (an API call gets 401).
    private static async Task ChallengeAsync(HttpContext context, AuthorizationPolicy policy)
    {
        // RFC 6750's error for a revoked token. OpenIddict's validation (AuthService's own admin API) reads these
        // items; without them it answers a challenge for a valid token with 403 (insufficient_access). Other
        // schemes ignore them.
        var properties = new AuthenticationProperties(new Dictionary<string, string?>
        {
            [".error"] = "invalid_token",
            [".error_description"] = "The Kale session has ended or isn't active any more.",
        });

        if (policy.AuthenticationSchemes.Count == 0)
        {
            await context.ChallengeAsync(properties);
            return;
        }

        foreach (var scheme in policy.AuthenticationSchemes)
        {
            await context.ChallengeAsync(scheme, properties);
        }
    }
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
        services.AddSingleton<IAuthorizationHandler, KaleSessionHandler>();

        // Replaces ASP.NET Core's default (registered by AddAuthorization, which it still uses for other refusals),
        // but not one the application registered itself: ResultHandlerCheck then says what to do.
        var registered = services.LastOrDefault(service => service.ServiceType == typeof(IAuthorizationMiddlewareResultHandler));
        if (registered?.ImplementationType == typeof(AuthorizationMiddlewareResultHandler))
        {
            services.AddSingleton<IAuthorizationMiddlewareResultHandler, KaleAuthorizationResultHandler>();
        }

        // Stops startup when an endpoint's [Authorize] uses another enum than this application's, or when the
        // application's own result handler would lose Kale's 401 and 503.
        services.AddTransient<IStartupFilter, OperationsEnumCheck>();
        services.AddTransient<IStartupFilter, ResultHandlerCheck>();
        return services;
    }
}

/// <summary>
/// Stops startup when the application has its own <see cref="IAuthorizationMiddlewareResultHandler"/> (registered
/// before or after AddKaleAuth): with it, a refused [Authorize(Operation)] would silently answer 403 again instead
/// of 401 or 503.
/// </summary>
internal sealed class ResultHandlerCheck(IAuthorizationMiddlewareResultHandler handler) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        if (handler is not KaleAuthorizationResultHandler)
        {
            throw new InvalidOperationException(
                $"{handler.GetType().FullName} is registered as IAuthorizationMiddlewareResultHandler, which would answer " +
                $"Kale's refusals with 403. Derive it from Auth.Client.{nameof(KaleAuthorizationResultHandler)} and call " +
                "base.HandleAsync for what it doesn't handle itself (it answers 401 for an ended Kale session and 503 when " +
                "AuthService can't be reached).");
        }

        next(app);
    };
}
