using System.Net;
using AuthService.SignIn;

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
    public void Requests_without_an_address_share_one_budget()
    {
        using var limiter = new SignInAttemptLimiter(new SignInAttemptSettings(AttemptsPerAddress: 1, Window: TimeSpan.FromMinutes(10)));

        limiter.TryAttempt(null).ShouldBeTrue();
        limiter.TryAttempt(null).ShouldBeFalse();
    }
}
