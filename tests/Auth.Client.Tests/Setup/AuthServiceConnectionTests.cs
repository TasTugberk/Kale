using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Time.Testing;

namespace Auth.Client.Tests.Setup;

/// <summary>The handler under the connection's HTTP client and gRPC channel. Creating a connection sends nothing.</summary>
public sealed class AuthServiceConnectionTests
{
    [Fact]
    public void Its_own_connections_are_replaced_now_and_then_so_a_changed_address_is_seen()
    {
        using var connection = Connection(backchannelHandler: null);

        var handler = connection.Handler.ShouldBeOfType<SocketsHttpHandler>();
        handler.PooledConnectionLifetime.ShouldBe(TimeSpan.FromMinutes(5));
        handler.EnableMultipleHttp2Connections.ShouldBeTrue("what gRPC's own handler does");
    }

    [Fact]
    public void Its_own_connections_send_keep_alive_pings_so_a_silent_AuthService_is_noticed()
    {
        // The invalidation stream is idle most of the time: without pings, a dead connection would go unnoticed and
        // the caches would keep their long lifetimes while no events arrive.
        using var connection = Connection(backchannelHandler: null);

        var handler = connection.Handler.ShouldBeOfType<SocketsHttpHandler>();
        handler.KeepAlivePingDelay.ShouldBe(TimeSpan.FromSeconds(30));
        handler.KeepAlivePingTimeout.ShouldBe(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void A_handler_from_the_application_is_used_as_it_is()
    {
        using var applicationHandler = new HttpClientHandler();

        using var connection = Connection(applicationHandler);

        connection.Handler.ShouldBeSameAs(applicationHandler);
    }

    private static AuthServiceConnection Connection(HttpMessageHandler? backchannelHandler) => new(
        new KaleAuthOptions
        {
            Authority = new Uri("https://auth.example/"),
            ApplicationKey = "billing",
            ClientSecret = "secret",
            BackchannelHandler = backchannelHandler,
        },
        new FakeTimeProvider(),
        new HostingEnvironment { EnvironmentName = "Production" });
}
