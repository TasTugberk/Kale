using Auth.Client.Tests.Authorization;
using Auth.Contracts;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Auth.Client.Tests.Invalidation;

/// <summary>
/// The listener's side of invalidation_stream.proto, over a fake backchannel: when it counts as connected (clears
/// the caches, longer session lifetime), and what changes when the stream ends. (Against the real AuthService:
/// EndToEnd.Tests.InvalidationTests.)
/// </summary>
public sealed class InvalidationStreamTests : IAsyncLifetime
{
    private static readonly Guid Accountant = Guid.CreateVersion7();

    private readonly FakeBackchannel _backchannel = new();
    private readonly FakeAuthService _authService = new();
    private readonly FakeTimeProvider _clock = new();
    private readonly ListLogger<InvalidationListener> _logger = new();
    private readonly AuthServiceConnection _connection;
    private readonly SessionAccess _access;
    private readonly InvalidationListener _listener;

    public InvalidationStreamTests()
    {
        _authService.Sessions["s-1"] = new SessionInfo(IsActive: true, RoleId: Accountant);
        _authService.RoleOperations[Accountant] = ["billing.InvoiceRead"];
        _connection = _backchannel.Connect(_clock);
        _access = new SessionAccess(_authService, _authService, _clock);
        _listener = new InvalidationListener(_connection, _access, _logger);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _listener.StopAsync(CancellationToken.None);
        _listener.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_refused_subscription_keeps_the_caches_and_the_short_session_lifetime()
    {
        // E.g. a deactivated application. grpc-dotnet reports a refusal as response headers too (its status comes
        // with the first read), so only AuthService's marker in the headers means "subscribed". Clearing the caches
        // here would throw away the answers that may stand in while AuthService is in trouble.
        _backchannel.GrpcAnswer = _ => Task.FromResult(FakeBackchannel.GrpcStatus(StatusCode.PermissionDenied));
        await _access.GetOperationsAsync("s-1");

        await _listener.StartAsync(CancellationToken.None);
        await Eventually(() => _logger.Entries.Any(e => e.Message.StartsWith("The invalidation stream ended", StringComparison.Ordinal)));

        _access.CachedSessionCount.ShouldBe(1, "the cache isn't cleared for a refusal");
        _logger.Entries.ShouldNotContain(e => e.Message.StartsWith("Receiving invalidation events", StringComparison.Ordinal));
    }

    [Fact]
    public async Task When_the_stream_ends_session_status_goes_back_to_its_20_second_lifetime()
    {
        var end = new TaskCompletionSource();
        _backchannel.GrpcAnswer = _ => Task.FromResult(
            FakeBackchannel.OpenStream(new Metadata { { InvalidationStreamHeaders.Subscribed, "1" } }, end.Task));
        await _listener.StartAsync(CancellationToken.None);
        await Eventually(() => _access.ReceivingEvents);
        await _access.GetOperationsAsync("s-1");

        // Reconnecting is refused, so the listener stays disconnected.
        _backchannel.GrpcAnswer = _ => Task.FromResult(FakeBackchannel.GrpcStatus(StatusCode.Unavailable));
        end.SetResult();
        await Eventually(() => !_access.ReceivingEvents);

        _clock.Advance(SessionAccess.SessionStatusLifetime);
        await _access.GetOperationsAsync("s-1");
        _authService.SessionCalls.ShouldBe(2);
    }

    [Fact]
    public async Task A_handler_from_the_application_without_keep_alive_pings_is_warned_about_at_startup()
    {
        // FakeBackchannel is such a handler. Without pings a silently dead connection isn't noticed, and session
        // status would keep its 2-minute lifetime with no events arriving.
        _backchannel.GrpcAnswer = _ => Task.FromResult(FakeBackchannel.GrpcStatus(StatusCode.Unavailable));

        await _listener.StartAsync(CancellationToken.None);

        await Eventually(() => _logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("keep-alive", StringComparison.Ordinal)));
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "it never happened");
            await Task.Delay(20);
        }
    }
}
