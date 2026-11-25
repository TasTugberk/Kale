using System.Net;
using AuthService.SignIn;
using Microsoft.Extensions.Configuration;

namespace AuthService.Tests.SignIn;

/// <summary>Each client address has its own budget of sign-in attempts per window.</summary>
public sealed class SignInAttemptLimiterTests
{
    [Fact]
    public void Each_address_has_its_own_attempts()
    {
        using var limiter = new SignInAttemptLimiter(new SignInAttemptSettings(AttemptsPerAddress: 2, Window: TimeSpan.FromMinutes(10)));
        var first = IPAddress.Parse("192.0.2.1");
        var second = IPAddress.Parse("192.0.2.2");

        limiter.TryAttempt(first).ShouldBeTrue();
        limiter.TryAttempt(first).ShouldBeTrue();
        limiter.TryAttempt(first).ShouldBeFalse();

        limiter.TryAttempt(second).ShouldBeTrue("another address isn't limited by the first one's attempts");
    }

    [Fact]
    public void The_limit_is_off_by_default()
    {
        // Behind a reverse proxy every client has the proxy's address: a default limit would be one budget for
        // everybody (until forwarded headers are supported, #72).
        var settings = SignInAttemptSettings.From(new ConfigurationBuilder().Build());
        using var limiter = new SignInAttemptLimiter(settings);

        settings.AttemptsPerAddress.ShouldBe(0);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            limiter.TryAttempt(IPAddress.Parse("192.0.2.1")).ShouldBeTrue();
        }
    }

    [Theory]
    [InlineData("-1", "00:01:00")]
    [InlineData("5", "00:00:00")]
    public void Invalid_settings_stop_startup(string attempts, string window)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SignIn:AttemptsPerAddress"] = attempts,
            ["SignIn:AttemptWindow"] = window,
        }).Build();

        Should.Throw<InvalidOperationException>(() => SignInAttemptSettings.From(configuration));
    }

    [Fact]
    public void Ipv6_addresses_in_one_64_prefix_share_a_budget()
    {
        // One client usually holds a whole /64 and could take a fresh address for every attempt.
        using var limiter = new SignInAttemptLimiter(new SignInAttemptSettings(AttemptsPerAddress: 1, Window: TimeSpan.FromMinutes(10)));

        limiter.TryAttempt(IPAddress.Parse("2001:db8:1:2::1")).ShouldBeTrue();
        limiter.TryAttempt(IPAddress.Parse("2001:db8:1:2:ffff:ffff:ffff:ffff")).ShouldBeFalse("same /64");
        limiter.TryAttempt(IPAddress.Parse("2001:db8:1:3::1")).ShouldBeTrue("another /64");
    }

    [Fact]
    public void An_ipv4_mapped_ipv6_address_counts_as_its_ipv4_address()
    {
        using var limiter = new SignInAttemptLimiter(new SignInAttemptSettings(AttemptsPerAddress: 1, Window: TimeSpan.FromMinutes(10)));

        limiter.TryAttempt(IPAddress.Parse("192.0.2.7")).ShouldBeTrue();
        limiter.TryAttempt(IPAddress.Parse("::ffff:192.0.2.7")).ShouldBeFalse("the same client, seen over IPv6");
    }

    [Fact]
    public void Requests_without_an_address_share_one_budget()
    {
        using var limiter = new SignInAttemptLimiter(new SignInAttemptSettings(AttemptsPerAddress: 1, Window: TimeSpan.FromMinutes(10)));

        limiter.TryAttempt(null).ShouldBeTrue();
        limiter.TryAttempt(null).ShouldBeFalse();
    }
}
