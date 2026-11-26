using System.Net;
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
    public bool IsAnyTrusted => Proxies.Count > 0 || Networks.Count > 0;

    /// <summary>Reads the settings; a value that isn't an address or a network stops startup.</summary>
    public static TrustedProxies From(IConfiguration configuration)
    {
        var proxies = new List<IPAddress>();
        foreach (var value in Values(configuration, "ForwardedHeaders:KnownProxies"))
        {
            // IPAddress.TryParse alone would also accept e.g. "10" (as 0.0.0.10); a dotted or colon form is required.
            if (!IPAddress.TryParse(value, out var address) || !(value.Contains('.') || value.Contains(':')))
            {
                throw new InvalidOperationException($"ForwardedHeaders:KnownProxies: '{value}' is not an IP address.");
            }

            proxies.Add(address);
        }

        var networks = new List<IPNetwork>();
        foreach (var value in Values(configuration, "ForwardedHeaders:KnownNetworks"))
        {
            if (!value.Contains('/') || !IPNetwork.TryParse(value, out var network))
            {
                throw new InvalidOperationException(
                    $"ForwardedHeaders:KnownNetworks: '{value}' is not a network in CIDR form (e.g. 10.89.0.0/24).");
            }

            // Trusting every address would let any client claim any address and scheme.
            if (network.PrefixLength == 0)
            {
                throw new InvalidOperationException(
                    $"ForwardedHeaders:KnownNetworks: '{value}' would trust every address. List the proxies' addresses or networks.");
            }

            networks.Add(network);
        }

        return new TrustedProxies(proxies, networks);
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
