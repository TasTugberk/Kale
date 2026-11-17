using System.Net;
using Auth.Contracts.V1;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Auth.Client.Tests.Setup;

/// <summary>
/// The connection's own handlers (no BackchannelHandler) against a real local server, the way an application
/// reaches AuthService: the other tests use an in-memory handler and can't see what happens on real sockets.
/// </summary>
public sealed class RealConnectionTests : IAsyncLifetime
{
    private const string AppToken = "app-token";

    private WebApplication _server = null!;
    private ListenOptions _http1 = null!;
    private ListenOptions _grpc = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // Like the local stack: the token endpoint on plain HTTP/1.1, gRPC (h2c) on an HTTP/2-only endpoint.
            kestrel.Listen(IPAddress.Loopback, 0, listen => _http1 = listen);
            kestrel.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
                _grpc = listen;
            });
        });
        builder.Services.AddGrpc();

        _server = builder.Build();
        _server.MapPost("/connect/token", () => Results.Json(new { access_token = AppToken, expires_in = 300 }));
        _server.MapGrpcService<EchoTokenSessions>();
        await _server.StartAsync();
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task A_gRPC_call_gets_its_token_and_reaches_AuthService_over_real_connections()
    {
        using var connection = new AuthServiceConnection(
            new KaleAuthOptions
            {
                Authority = new Uri($"http://127.0.0.1:{_http1.IPEndPoint!.Port}/"),
                GrpcAddress = new Uri($"http://127.0.0.1:{_grpc.IPEndPoint!.Port}/"),
                AllowInsecureHttpDevOnly = true,
                ApplicationKey = "billing",
                ClientSecret = "secret",
            },
            new FakeTimeProvider(DateTimeOffset.UtcNow),
            new HostingEnvironment { EnvironmentName = "Testing" });

        var response = await connection.Sessions.GetSessionAsync(
            new GetSessionRequest { SessionId = Guid.CreateVersion7().ToString() },
            deadline: DateTime.UtcNow.AddSeconds(10));

        response.UserId.ShouldBe($"Bearer {AppToken}");
    }

    /// <summary>Answers with the Authorization header it got, so the test sees the token arrive.</summary>
    private sealed class EchoTokenSessions : SessionService.SessionServiceBase
    {
        public override Task<GetSessionResponse> GetSession(GetSessionRequest request, ServerCallContext context) =>
            Task.FromResult(new GetSessionResponse { UserId = context.RequestHeaders.GetValue("authorization") ?? "" });
    }
}
