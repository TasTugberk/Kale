using System.Net;
using System.Threading.RateLimiting;

namespace AuthService.SignIn;

/// <param name="AttemptsPerAddress">SignIn:AttemptsPerAddress (default 20): passwords and two-step codes one client
/// address may send per window, across all accounts.</param>
/// <param name="Window">SignIn:AttemptWindow (default 1 minute).</param>
public sealed record SignInAttemptSettings(int AttemptsPerAddress, TimeSpan Window)
{
    public static SignInAttemptSettings From(IConfiguration configuration)
    {
        var settings = new SignInAttemptSettings(
            configuration.GetValue("SignIn:AttemptsPerAddress", defaultValue: 20),
            configuration.GetValue("SignIn:AttemptWindow", defaultValue: TimeSpan.FromMinutes(1)));
        if (settings.AttemptsPerAddress < 1 || settings.Window <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("SignIn:AttemptsPerAddress must be at least 1 and SignIn:AttemptWindow positive.");
        }

        return settings;
    }
}

/// <summary>
/// Limits sign-in attempts (a password on the login page, a code on the two-step page) per client address, with
/// .NET's built-in rate limiter: a fixed window per address. The account lockout (5 wrong attempts) protects one
/// account; this keeps one address from guessing across many. It uses the connection's address: behind a reverse
/// proxy every client has the proxy's address (forwarded headers aren't supported yet, issue #72), so they share
/// one budget there. Per instance: with several instances, an address gets the budget on each.
/// </summary>
/// <remarks>
/// Not the rate-limiting middleware: the pages call this themselves, so a refused attempt shows the page again with
/// a message (in the user's language) instead of a bare 429, and only the POSTs count, not opening the page.
/// </remarks>
public sealed class SignInAttemptLimiter(SignInAttemptSettings settings) : IDisposable
{
    // Requests without an address (in-process test servers) share one budget.
    private readonly PartitionedRateLimiter<IPAddress?> _limiter = PartitionedRateLimiter.Create<IPAddress?, string>(
        address => RateLimitPartition.GetFixedWindowLimiter(
            address?.ToString() ?? "none",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = settings.AttemptsPerAddress,
                Window = settings.Window,
                QueueLimit = 0,
            }));

    /// <summary>Counts one attempt; false when the address has used up its attempts for this window.</summary>
    public bool TryAttempt(IPAddress? address)
    {
        using var lease = _limiter.AttemptAcquire(address);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
