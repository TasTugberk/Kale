using Auth.Client.Tests.Authorization;
using Grpc.Core;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;

namespace Auth.Client.Tests.Setup;

/// <summary>
/// The "kale" health check says whether the operations are registered: an application whose enum Kale rejected
/// keeps running but refuses every protected request, so whoever deploys it must be able to see that.
/// </summary>
public sealed class RegistrationHealthTests : IDisposable
{
    private readonly FakeBackchannel _backchannel = new();
    private readonly AuthServiceConnection _connection;
    private readonly OperationRegistrationStatus _status = new();
    private readonly OperationRegistrationService _registration;

    // The registration's retry waits run on this clock; the test moves it along.
    private readonly FakeTimeProvider _clock = new();

    public RegistrationHealthTests()
    {
        _connection = _backchannel.Connect();
        _registration = new OperationRegistrationService(
            _connection, OperationCatalog.For<BillingOperations>("billing"), _clock,
            new ListLogger<OperationRegistrationService>(), _status);
    }

    public void Dispose()
    {
        _registration.Dispose();
        _connection.Dispose();
        _backchannel.Dispose();
    }

    [Fact]
    public async Task Until_the_first_registration_the_check_is_degraded_not_unhealthy()
    {
        // Kale down at the application's start: it registers once Kale is back. Degraded still answers 200 on
        // /health, so a container whose Kale starts later isn't marked unhealthy for it.
        var unavailableAnswers = 0;
        _backchannel.GrpcAnswer = _ =>
        {
            Interlocked.Increment(ref unavailableAnswers);
            return Task.FromResult(FakeBackchannel.GrpcStatus(StatusCode.Unavailable));
        };
        await _registration.StartAsync(CancellationToken.None);

        // At least two failed attempts (a retry happened), so this checks what a failure leaves, not the initial state.
        for (var attempt = 0; attempt < 200 && Volatile.Read(ref unavailableAnswers) < 2; attempt++)
        {
            _clock.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(10);
        }

        Volatile.Read(ref unavailableAnswers).ShouldBeGreaterThanOrEqualTo(2);
        var result = await Check();

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description!.ShouldContain("not registered");
        await _registration.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Operations_Kale_rejected_make_the_check_unhealthy_with_the_reason()
    {
        _backchannel.GrpcAnswer = _ => Task.FromResult(FakeBackchannel.GrpcStatus(StatusCode.InvalidArgument, "Implication cycle: A → B → A."));
        await _registration.StartAsync(CancellationToken.None);

        var result = await WaitForStatus(HealthStatus.Unhealthy);

        result.Description!.ShouldContain("Implication cycle");
        await _registration.StopAsync(CancellationToken.None);
    }

    private Task<HealthCheckResult> Check() =>
        new KaleRegistrationHealthCheck(_status).CheckHealthAsync(new HealthCheckContext());

    private async Task<HealthCheckResult> WaitForStatus(HealthStatus expected)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var result = await Check();
            if (result.Status == expected)
            {
                return result;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"The check never became {expected}.");
    }
}
