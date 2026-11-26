using System.Net;
using AuthService.Hosting;
using Microsoft.Extensions.Configuration;

namespace AuthService.Tests.Hosting;

/// <summary>ForwardedHeaders:KnownProxies and :KnownNetworks: read and checked at startup; nothing trusted by default.</summary>
public sealed class TrustedProxiesTests
{
    [Fact]
    public void Nothing_is_trusted_by_default()
    {
        var proxies = TrustedProxies.From(Configuration());

        proxies.IsAnyTrusted.ShouldBeFalse();
    }

    [Fact]
    public void Addresses_and_networks_are_read()
    {
        var proxies = TrustedProxies.From(Configuration(
            ("ForwardedHeaders:KnownProxies:0", "10.0.0.5"),
            ("ForwardedHeaders:KnownProxies:1", "2001:db8::5"),
            ("ForwardedHeaders:KnownNetworks:0", "10.89.0.0/24")));

        proxies.IsAnyTrusted.ShouldBeTrue();
        proxies.Proxies.ShouldBe([IPAddress.Parse("10.0.0.5"), IPAddress.Parse("2001:db8::5")]);
        proxies.Networks.ShouldBe([IPNetwork.Parse("10.89.0.0/24")]);
    }

    [Theory]
    [InlineData("ForwardedHeaders:KnownProxies:0", "proxy.example")]
    [InlineData("ForwardedHeaders:KnownProxies:0", "10.0.0.0/8")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "10.0.0.5")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "10.0.0.0/33")]
    public void A_value_that_is_not_an_address_or_network_stops_startup(string key, string value)
    {
        var error = Should.Throw<InvalidOperationException>(() => TrustedProxies.From(Configuration((key, value))));

        error.Message.ShouldContain(value);
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    public void Trusting_every_address_is_refused(string network)
    {
        var error = Should.Throw<InvalidOperationException>(() =>
            TrustedProxies.From(Configuration(("ForwardedHeaders:KnownNetworks:0", network))));

        error.Message.ShouldContain("every address");
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();
}
