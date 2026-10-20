using System.Net;
using System.Net.Sockets;
using Auth.Contracts.V1;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using Grpc.Core;
using Grpc.Net.Client;

namespace AuthService.Tests.Grpc;

/// <summary>
/// Local compose has no TLS, and gRPC needs HTTP/2. Kestrel speaks HTTP/2 over plain HTTP (h2c) only on an
/// endpoint limited to HTTP/2, so local setups add one, allowed only with the dev-only flag. Real Kestrel here,
/// not the in-memory test server, because the protocol is the point.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class InsecureGrpcTests(PostgresFixture postgres)
{
    [Fact]
    public async Task With_the_dev_only_flag_an_HTTP2_only_endpoint_serves_gRPC_over_plain_HTTP()
    {
        var (httpPort, grpcPort) = (FreePort(), FreePort());
        await using var app = await StartOnKestrel(httpPort, grpcPort);

        var error = await CallWithoutToken(grpcPort);

        // AuthService answered (it wants a token), so the call itself got through.
        error.StatusCode.ShouldBe(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task The_ordinary_plain_HTTP_endpoint_cannot_serve_gRPC()
    {
        // Why the extra endpoint exists: without TLS, Kestrel's default endpoint speaks only HTTP/1.1.
        var (httpPort, grpcPort) = (FreePort(), FreePort());
        await using var app = await StartOnKestrel(httpPort, grpcPort);

        // The endpoint is up and serves HTTP/1.1 ...
        using var http = new HttpClient();
        (await http.GetAsync(new Uri($"http://127.0.0.1:{httpPort}/health"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        // ... but a gRPC call there never reaches AuthService.
        var error = await CallWithoutToken(httpPort);

        error.StatusCode.ShouldNotBe(StatusCode.Unauthenticated);
    }

    private async Task<AuthServiceApp> StartOnKestrel(int httpPort, int grpcPort)
    {
        var app = await AuthServiceApp.CreateAsync(postgres,
            ("Kestrel:Endpoints:Http:Url", $"http://127.0.0.1:{httpPort}"),
            ("Kestrel:Endpoints:GrpcDevOnly:Url", $"http://127.0.0.1:{grpcPort}"),
            ("Kestrel:Endpoints:GrpcDevOnly:Protocols", "Http2"),
            ("Grpc:AllowInsecureDevOnly", "true"));
        app.UseKestrel();
        app.StartServer();
        return app;
    }

    private static async Task<RpcException> CallWithoutToken(int port)
    {
        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}");
        var registration = new OperationRegistration.OperationRegistrationClient(channel);
        return await Should.ThrowAsync<RpcException>(() =>
            registration.RegisterOperationsAsync(new RegisterOperationsRequest(), deadline: DateTime.UtcNow.AddSeconds(10)).ResponseAsync);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
