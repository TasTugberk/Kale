using System.Net;
using System.Net.Sockets;
using ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders;
using IPNetwork = System.Net.IPNetwork;

namespace AuthService.Hosting;

/// <summary>
/// The reverse proxies whose X-Forwarded-Proto and X-Forwarded-For headers AuthService believes
/// (ForwardedHeaders:KnownProxies, addresses; ForwardedHeaders:KnownNetworks, CIDR networks). Nothing by default:
/// a header is only as trustworthy as whoever sent it, and anyone can send one.
/// </summary>
public sealed record TrustedProxies(IReadOnlyList<IPAddress> Proxies, IReadOnlyList<IPNetwork> Networks)
{
    // The widest networks accepted. Proxies sit in a small network of their own; anything wider is almost certainly
    // a typo or "just make it work", and would let most of the internet claim any address.
    private const int NarrowestIPv4Prefix = 8;
    private const int NarrowestIPv6Prefix = 32;

    public bool IsAnyTrusted => Proxies.Count > 0 || Networks.Count > 0;

    /// <summary>Reads the settings; a value that isn't an address or a network stops startup.</summary>
    public static TrustedProxies From(IConfiguration configuration)
    {
        // ASPNETCORE_FORWARDEDHEADERS_ENABLED adds ASP.NET Core's own forwarded-headers step, which believes the
        // headers from every sender (it clears the known proxies). Only the lists below may decide whom to believe.
        if (configuration.GetValue("FORWARDEDHEADERS_ENABLED", defaultValue: false))
        {
            throw new InvalidOperationException(
                "ASPNETCORE_FORWARDEDHEADERS_ENABLED would believe X-Forwarded-* headers from every sender. Remove it and " +
                "list your reverse proxies in ForwardedHeaders:KnownProxies or ForwardedHeaders:KnownNetworks instead.");
        }

        var proxies = new List<IPAddress>();
        foreach (var value in Values(configuration, "ForwardedHeaders:KnownProxies"))
        {
            if (!IPAddress.TryParse(value, out var address) || !IsWrittenPlainly(value, address))
            {
                throw new InvalidOperationException($"ForwardedHeaders:KnownProxies: '{value}' is not an IP address.");
            }

            proxies.Add(address);
        }

        var networks = new List<IPNetwork>();
        foreach (var value in Values(configuration, "ForwardedHeaders:KnownNetworks"))
        {
            if (!value.Contains('/') || !IPNetwork.TryParse(value, out var network)
                || !IsWrittenPlainly(value[..value.IndexOf('/')], network.BaseAddress))
            {
                throw new InvalidOperationException(
                    $"ForwardedHeaders:KnownNetworks: '{value}' is not a network in CIDR form (e.g. 10.89.0.0/24).");
            }

            // Trusting (nearly) every address would let any client claim any address and scheme.
            var narrowest = network.BaseAddress.AddressFamily == AddressFamily.InterNetworkV6 ? NarrowestIPv6Prefix : NarrowestIPv4Prefix;
            if (network.PrefixLength < narrowest)
            {
                throw new InvalidOperationException(
                    $"ForwardedHeaders:KnownNetworks: '{value}' is too wide (at most /{NarrowestIPv4Prefix} for IPv4, " +
                    $"/{NarrowestIPv6Prefix} for IPv6). List the proxies' own addresses or their small network.");
            }

            networks.Add(network);
        }

        return new TrustedProxies(proxies, networks);
    }

    // IPAddress.TryParse also accepts forms nobody means in a setting: "10" (0.0.0.10), and a leading zero is
    // octal ("010.0.0.5" is 8.0.0.5, a public address). So an IPv4 address must be in its usual dotted form, and
    // anything else must at least be IPv6 (contain ':').
    private static bool IsWrittenPlainly(string value, IPAddress address)
    {
        return address.AddressFamily == AddressFamily.InterNetwork
            ? address.ToString() == value
            : value.Contains(':');
    }

    /// <summary>
    /// X-Forwarded-For (the client's address: sign-in limit, logs) and X-Forwarded-Proto (https at the proxy, so
    /// OpenIddict and the cookies see https). Not X-Forwarded-Host: the proxy passes the public Host header on
    /// as it is, and the issuer is fixed by Auth:Issuer anyway.
    /// </summary>
    public void Apply(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        // ASP.NET Core trusts loopback by default; only what's configured counts here.
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in Proxies)
        {
            options.KnownProxies.Add(proxy);
        }

        foreach (var network in Networks)
        {
            options.KnownIPNetworks.Add(network);
        }

        // Walk back through every trusted proxy in a chain (e.g. a load balancer, then the proxy) and stop at the
        // first address that isn't one: that's the client. The default (1) would stop at the second proxy.
        options.ForwardLimit = null;
    }

    private static IEnumerable<string> Values(IConfiguration configuration, string key) =>
        configuration.GetSection(key).GetChildren()
            .Select(child => child.Value?.Trim())
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(value => value!);
}
