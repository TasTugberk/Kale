using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;

namespace AuthService.SignIn;

/// <param name="AttemptsPerAddress">SignIn:AttemptsPerAddress: passwords and two-step codes one client address may
/// send per window, across all accounts. 0 (the default) turns the limit off.</param>
/// <param name="Window">SignIn:AttemptWindow (default 1 minute).</param>
public sealed record SignInAttemptSettings(int AttemptsPerAddress, TimeSpan Window)
{
    public bool IsOn => AttemptsPerAddress > 0;

    public static SignInAttemptSettings From(IConfiguration configuration)
    {
        // Off by default: AuthService sees the connection's address, and behind a reverse proxy (the documented
        // production setup) that is the proxy's for every client until forwarded headers are supported (#72). One
        // budget for everybody would refuse a normal morning's sign-ins, and let one anonymous script block them all.
        var settings = new SignInAttemptSettings(
            configuration.GetValue("SignIn:AttemptsPerAddress", defaultValue: 0),
            configuration.GetValue("SignIn:AttemptWindow", defaultValue: TimeSpan.FromMinutes(1)));
        if (settings.AttemptsPerAddress < 0 || settings.Window <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "SignIn:AttemptsPerAddress must be 0 (off) or more, and SignIn:AttemptWindow positive.");
        }

        return settings;
    }
}

/// <summary>
/// Limits sign-in attempts (a password on the login page, a code on the two-step page) per client address, with
/// .NET's built-in rate limiter: a fixed window per address. The account lockout (5 wrong attempts) protects one
/// account; this keeps one address from guessing across many. Off unless SignIn:AttemptsPerAddress is set, and only
/// useful where AuthService sees the real client addresses (see <see cref="SignInAttemptSettings.From"/>). Per
/// instance: with several instances, an address gets the budget on each.
/// </summary>
/// <remarks>
/// Not the rate-limiting middleware: the pages call this themselves, so a refused attempt shows the page again with
/// a message (in the user's language) instead of a bare 429, and only the POSTs count, not opening the page.
/// </remarks>
public sealed class SignInAttemptLimiter(SignInAttemptSettings settings) : IDisposable
{
    private readonly PartitionedRateLimiter<IPAddress?> _limiter = PartitionedRateLimiter.Create<IPAddress?, string>(
        address => RateLimitPartition.GetFixedWindowLimiter(
            ClientKey(address),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = settings.AttemptsPerAddress,
                Window = settings.Window,
                QueueLimit = 0,
            }));

    /// <summary>How long a refused address waits at most: the window (for Retry-After).</summary>
    public TimeSpan Window => settings.Window;

    /// <summary>Counts one attempt; false when the address has used up its attempts for this window.</summary>
    public bool TryAttempt(IPAddress? address)
    {
        if (!settings.IsOn)
        {
            return true;
        }

        using var lease = _limiter.AttemptAcquire(address);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();

    // Which client an address stands for. An IPv6 client usually holds a whole /64 and could take a new address
    // for every attempt, so IPv6 counts per /64. An IPv4 client reached over a dual-stack socket arrives as
    // ::ffff:a.b.c.d, which is the same client as a.b.c.d. Requests without an address (in-process test servers)
    // share one budget.
    internal static string ClientKey(IPAddress? address)
    {
        if (address is null)
        {
            return "none";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }
}
