using Auth.Client.Tests.Authorization;
using Auth.Contracts.V1;
using Google.Protobuf;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Auth.Client.Tests.Invalidation;

/// <summary>
/// DESIGN.md cache invalidation: each event evicts the matching cache entry, so the next request asks AuthService
/// again instead of waiting for the cache time.
/// </summary>
public sealed class InvalidationListenerTests
{
    private static readonly Guid Accountant = Guid.CreateVersion7();

    private readonly FakeAuthService _authService = new();
    private readonly SessionAccess _access;

    public InvalidationListenerTests()
    {
        _authService.Sessions["s-1"] = new SessionInfo(IsActive: true, RoleId: Accountant);
        _authService.RoleOperations[Accountant] = ["billing.InvoiceRead"];
        _access = new SessionAccess(_authService, _authService, new FakeTimeProvider());
    }

    [Fact]
    public async Task A_stopped_session_is_checked_with_AuthService_again()
    {
        await _access.GetOperationsAsync("s-1");

        InvalidationListener.TryApply(new InvalidationEvent { SessionStopped = new SessionStopped { SessionId = "s-1" } }.ToByteArray(), _access);
        await _access.GetOperationsAsync("s-1");

        _authService.SessionCalls.ShouldBe(2);
        _authService.RoleCalls.ShouldBe(1);
    }

    [Fact]
    public async Task An_edited_roles_operations_are_loaded_again()
    {
        await _access.GetOperationsAsync("s-1");

        InvalidationListener.TryApply(new InvalidationEvent { RoleEdited = new RoleEdited { RoleId = Accountant.ToString() } }.ToByteArray(), _access);
        await _access.GetOperationsAsync("s-1");

        _authService.RoleCalls.ShouldBe(2);
        _authService.SessionCalls.ShouldBe(1);
    }

    [Fact]
    public async Task A_disabled_user_needs_nothing_more_than_the_session_stopped_events_that_come_with_it()
    {
        await _access.GetOperationsAsync("s-1");

        InvalidationListener.TryApply(new InvalidationEvent { UserDisabled = new UserDisabled { UserId = Guid.CreateVersion7().ToString() } }.ToByteArray(), _access);
        await _access.GetOperationsAsync("s-1");

        _authService.SessionCalls.ShouldBe(1);
        _authService.RoleCalls.ShouldBe(1);
    }

    [Fact]
    public async Task An_unreadable_message_is_reported_and_changes_nothing()
    {
        await _access.GetOperationsAsync("s-1");

        InvalidationListener.TryApply([0xFF, 0xFF, 0xFF], _access).ShouldBeFalse();
        var badRoleId = new InvalidationEvent { RoleEdited = new RoleEdited { RoleId = "not-a-uuid" } };
        InvalidationListener.TryApply(badRoleId.ToByteArray(), _access).ShouldBeFalse();
        await _access.GetOperationsAsync("s-1");

        _authService.SessionCalls.ShouldBe(1);
        _authService.RoleCalls.ShouldBe(1);
    }

    [Fact]
    public void A_readable_message_is_applied()
    {
        var stopped = new InvalidationEvent { SessionStopped = new SessionStopped { SessionId = "s-1" } };

        InvalidationListener.TryApply(stopped.ToByteArray(), _access).ShouldBeTrue();
    }

    [Fact]
    public void Outside_development_RabbitMQ_is_required()
    {
        var error = Should.Throw<InvalidOperationException>(() => Listener(rabbitMqUri: null, environment: "Production"));

        error.Message.ShouldContain("RabbitMqUri");
    }

    [Fact]
    public void Outside_development_plain_amqp_is_refused()
    {
        var error = Should.Throw<InvalidOperationException>(() => Listener(new Uri("amqp://rabbitmq/"), environment: "Production"));

        error.Message.ShouldContain("amqps");
    }

    [Fact]
    public void In_development_plain_amqp_or_no_RabbitMQ_at_all_is_allowed()
    {
        Should.NotThrow(() => Listener(new Uri("amqp://rabbitmq/"), environment: "Development"));
        Should.NotThrow(() => Listener(rabbitMqUri: null, environment: "Testing"));
    }

    private InvalidationListener Listener(Uri? rabbitMqUri, string environment) => new(
        new KaleAuthOptions { RabbitMqUri = rabbitMqUri },
        _access,
        new HostingEnvironment { EnvironmentName = environment },
        NullLogger<InvalidationListener>.Instance);
}
