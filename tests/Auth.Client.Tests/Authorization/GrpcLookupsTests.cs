using System.Diagnostics;
using System.Net;
using Grpc.Core;
using Microsoft.Extensions.Time.Testing;

namespace Auth.Client.Tests.Authorization;

/// <summary>
/// Which failures mean "AuthService couldn't be reached" (the last answer may then stand in for a while,
/// <see cref="SessionAccess.UsableWhileUnreachable"/>) and which are answers: "no session", or a refusal that fails
/// closed. Through a real gRPC client over a fake HTTP backchannel.
/// </summary>
public sealed class GrpcLookupsTests : IDisposable
{
    private readonly FakeBackchannel _backchannel = new();
    private readonly FakeTimeProvider _clock = new();
    private readonly AuthServiceConnection _connection;
    private readonly GrpcLookups _lookups;

    public GrpcLookupsTests()
    {
        _connection = _backchannel.Connect(_clock);
        _lookups = new GrpcLookups(_connection);
    }

    public void Dispose() => _connection.Dispose();

    // ----- Unreachable -----

    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.DeadlineExceeded)]
    public async Task A_call_that_got_no_answer_means_unreachable(StatusCode status)
    {
        _backchannel.GrpcAnswer = _ => Task.FromResult(FakeBackchannel.GrpcStatus(status));

        await Should.ThrowAsync<AuthServiceUnreachableException>(() => _lookups.GetAsync("s-1", CancellationToken.None));
        await Should.ThrowAsync<AuthServiceUnreachableException>(() => _lookups.GetAsync(Guid.CreateVersion7(), CancellationToken.None));
    }

    [Fact]
    public async Task A_refused_connection_means_unreachable()
    {
        _backchannel.GrpcAnswer = _ => throw new HttpRequestException("Connection refused");

        await Should.ThrowAsync<AuthServiceUnreachableException>(() => _lookups.GetAsync("s-1", CancellationToken.None));
    }

    [Fact]
    public async Task A_token_request_that_got_no_response_means_unreachable()
    {
        _backchannel.TokenAnswer = _ => throw new HttpRequestException("Connection refused");

        await Should.ThrowAsync<AuthServiceUnreachableException>(() => _lookups.GetAsync("s-1", CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_token_request_that_failed_on_the_server_side_means_unreachable(HttpStatusCode status)
    {
        // AuthService (or a proxy in front of it) in trouble.
        _backchannel.TokenAnswer = _ => Task.FromResult(new HttpResponseMessage(status));

        await Should.ThrowAsync<AuthServiceUnreachableException>(() => _lookups.GetAsync("s-1", CancellationToken.None));
    }

    [Fact]
    public async Task A_lookup_that_does_not_answer_within_the_lookup_timeout_means_unreachable()
    {
        _backchannel.GrpcAnswer = async cancellationToken =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        };
        var access = new SessionAccess(_lookups, _lookups, _clock);

        var request = access.GetSessionAsync("s-1");
        _clock.Advance(SessionAccess.LookupTimeout);

        await Should.ThrowAsync<AuthServiceUnreachableException>(() => request.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // ----- Answers -----

    [Theory]
    [InlineData(StatusCode.NotFound)]
    [InlineData(StatusCode.InvalidArgument)]
    public async Task An_unknown_session_or_one_that_is_not_even_a_uuid_is_no_session(StatusCode status)
    {
        _backchannel.GrpcAnswer = _ => Task.FromResult(FakeBackchannel.GrpcStatus(status));

        (await _lookups.GetAsync("s-1", CancellationToken.None)).ShouldBeNull();
    }

    [Theory]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unauthenticated)]
    public async Task A_refused_call_fails_closed(StatusCode status)
    {
        // E.g. the application was deactivated: an answer, so no old answer may stand in for it.
        _backchannel.GrpcAnswer = _ => Task.FromResult(FakeBackchannel.GrpcStatus(status));

        await Should.ThrowAsync<RpcException>(() => _lookups.GetAsync("s-1", CancellationToken.None));
        await Should.ThrowAsync<RpcException>(() => _lookups.GetAsync(Guid.CreateVersion7(), CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task A_refused_token_request_fails_closed(HttpStatusCode status)
    {
        // The application was deactivated or its secret changed: AuthService answered.
        _backchannel.TokenAnswer = _ => Task.FromResult(new HttpResponseMessage(status));

        await Should.ThrowAsync<RpcException>(() => _lookups.GetAsync("s-1", CancellationToken.None));
        await Should.ThrowAsync<RpcException>(() => _lookups.GetAsync(Guid.CreateVersion7(), CancellationToken.None));
    }
}
