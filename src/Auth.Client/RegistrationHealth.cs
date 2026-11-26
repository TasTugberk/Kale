using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Auth.Client;

/// <summary>Where the application's operation registration stands, for the "kale" health check.</summary>
internal sealed class OperationRegistrationStatus
{
    // Read by health checks while the registration service writes it: one immutable snapshot, swapped whole.
    private volatile Snapshot _current = new(RegistrationState.Pending, null);

    public RegistrationState State => _current.State;

    /// <summary>Kale's reason, when it rejected the operations.</summary>
    public string? RejectionReason => _current.RejectionReason;

    public void Registered() => _current = new Snapshot(RegistrationState.Registered, null);

    public void Rejected(string reason) => _current = new Snapshot(RegistrationState.Rejected, reason);

    private sealed record Snapshot(RegistrationState State, string? RejectionReason);
}

internal enum RegistrationState
{
    /// <summary>Not registered yet: Kale hasn't answered since the application started (it keeps trying).</summary>
    Pending,

    /// <summary>Kale accepted the operations at least once since the application started.</summary>
    Registered,

    /// <summary>Kale rejected them (INVALID_ARGUMENT, e.g. an implication cycle): nothing is retried.</summary>
    Rejected,
}

/// <summary>
/// The "kale" health check (registered by AddKaleAuth): whether this application's operations are registered.
/// Healthy once Kale accepted them. Degraded until then (Kale not reached yet: it keeps trying, and Degraded still
/// answers 200 on a default /health, so a container doesn't turn unhealthy just because Kale started later).
/// Unhealthy when Kale rejected them: the application then refuses every protected request until it's fixed and
/// deployed again, so whoever deploys it must see it. A later failed re-registration (every 5 minutes) doesn't
/// change a Healthy result: the operations registered earlier are still there.
/// </summary>
internal sealed class KaleRegistrationHealthCheck(OperationRegistrationStatus status) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(status.State switch
        {
            RegistrationState.Registered => HealthCheckResult.Healthy("Operations registered with Kale."),
            RegistrationState.Rejected => HealthCheckResult.Unhealthy(
                $"Kale rejected this application's operations, so every protected request is refused: {status.RejectionReason}"),
            _ => HealthCheckResult.Degraded("Operations not registered with Kale yet (Kale not reached; retrying)."),
        });
}
