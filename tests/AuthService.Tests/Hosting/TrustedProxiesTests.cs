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
    [InlineData("ForwardedHeaders:KnownProxies:0", "010.0.0.5")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "010.0.0.0/8")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "10.0.0.5")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "10.0.0.0/33")]
    public void A_value_that_is_not_an_address_or_network_stops_startup(string key, string value)
    {
        var error = Should.Throw<InvalidOperationException>(() => TrustedProxies.From(Configuration((key, value))));

        error.Message.ShouldContain(value);
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("0.0.0.0/1")]
    [InlineData("10.0.0.0/7")]
    [InlineData("::/0")]
    [InlineData("2000::/3")]
    [InlineData("2001:db8::/31")]
    public void A_network_wider_than_a_slash_8_or_an_IPv6_slash_32_is_refused(string network)
    {
        var error = Should.Throw<InvalidOperationException>(() =>
            TrustedProxies.From(Configuration(("ForwardedHeaders:KnownNetworks:0", network))));

        error.Message.ShouldContain("too wide");
    }

    [Theory]
    [InlineData("10.0.0.0/8")]
    [InlineData("2001:db8::/32")]
    public void The_widest_networks_allowed_are_a_slash_8_and_an_IPv6_slash_32(string network)
    {
        TrustedProxies.From(Configuration(("ForwardedHeaders:KnownNetworks:0", network))).IsAnyTrusted.ShouldBeTrue();
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    public void The_frameworks_own_switch_for_forwarded_headers_is_refused(string value)
    {
        // ASPNETCORE_FORWARDEDHEADERS_ENABLED (read as FORWARDEDHEADERS_ENABLED) adds ASP.NET Core's own forwarded
        // headers step, which would believe these headers from every sender.
        var error = Should.Throw<InvalidOperationException>(() =>
            TrustedProxies.From(Configuration(("FORWARDEDHEADERS_ENABLED", value))));

        error.Message.ShouldContain("ASPNETCORE_FORWARDEDHEADERS_ENABLED");
    }

    [Fact]
    public void The_frameworks_switch_set_to_false_is_fine()
    {
        TrustedProxies.From(Configuration(("FORWARDEDHEADERS_ENABLED", "false"))).IsAnyTrusted.ShouldBeFalse();
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();
}
