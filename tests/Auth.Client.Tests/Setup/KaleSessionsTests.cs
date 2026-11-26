using Auth.Client.Tests.Authorization;
using Grpc.Core;
using Microsoft.Extensions.Time.Testing;

namespace Auth.Client.Tests.Setup;

/// <summary>KaleSessions.EndAsync's documented answers when Kale doesn't end the session.</summary>
public sealed class KaleSessionsTests : IDisposable
{
    private readonly FakeBackchannel _backchannel = new();
    private readonly AuthServiceConnection _connection;
    private readonly KaleSessions _sessions;

    public KaleSessionsTests()
    {
        _connection = _backchannel.Connect();
        var lookups = new FakeAuthService();
        _sessions = new KaleSessions(_connection, new SessionAccess(lookups, lookups, new FakeTimeProvider()));
    }

    public void Dispose()
    {
        _connection.Dispose();
        _backchannel.Dispose();
    }

    [Theory]
    [InlineData(StatusCode.NotFound)]
    [InlineData(StatusCode.InvalidArgument)]
    public async Task An_unknown_or_malformed_session_is_false(StatusCode status)
    {
        _backchannel.GrpcAnswer = _ => Task.FromResult(FakeBackchannel.GrpcStatus(status));

        (await _sessions.EndAsync("s1")).ShouldBeFalse();
    }

    [Fact]
    public async Task When_Kale_cannot_be_reached_it_throws_the_unreachable_exception()
    {
        _backchannel.GrpcAnswer = _ => Task.FromResult(FakeBackchannel.GrpcStatus(StatusCode.Unavailable));

        await Should.ThrowAsync<AuthServiceUnreachableException>(() => _sessions.EndAsync("s1"));
    }

    [Fact]
    public async Task When_Kale_refuses_the_application_its_answer_comes_through()
    {
        // E.g. this application was deactivated: an answer, not an outage.
        _backchannel.GrpcAnswer = _ => Task.FromResult(FakeBackchannel.GrpcStatus(StatusCode.PermissionDenied));

        (await Should.ThrowAsync<RpcException>(() => _sessions.EndAsync("s1"))).StatusCode.ShouldBe(StatusCode.PermissionDenied);
    }
}
