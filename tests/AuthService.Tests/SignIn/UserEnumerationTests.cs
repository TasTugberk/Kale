using AuthService.Domain;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AuthService.Tests.SignIn;

/// <summary>
/// A failed login must take about as long for an unknown or disabled user as for a real one, so response time
/// doesn't reveal which user names exist. The slow part of a real login is hashing the password; these tests
/// check that it happens on every path.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class UserEnumerationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly CountingPasswordHasher _hasher = new();
    private AuthServiceApp _app = null!;

    public async Task InitializeAsync()
    {
        var baseApp = await AuthServiceApp.CreateAsync(postgres);
        _app = new AuthServiceApp(baseApp.ConnectionString)
        {
            ConfigureServices = services =>
            {
                services.RemoveAll<IPasswordHasher<User>>();
                services.AddSingleton<IPasswordHasher<User>>(_hasher);
            },
        };
        await baseApp.DisposeAsync();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task An_unknown_user_name_costs_a_password_hash_too()
    {
        using var browser = new Browser(_app);
        using var loginPage = await browser.FollowAsync(await browser.GetAsync(new Uri("/account/login", UriKind.Relative)));

        using var _ = await browser.LogInAsync(loginPage, "nobody", "whatever-password");

        _hasher.Verifications.ShouldBe(1);
    }

    [Fact]
    public async Task A_disabled_user_costs_a_password_hash_too()
    {
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = new User { UserName = "deniz", DisplayName = "Deniz", IsActive = false };
            (await users.CreateAsync(user, "Correct-horse-9")).Succeeded.ShouldBeTrue();
        }

        using var browser = new Browser(_app);
        using var loginPage = await browser.FollowAsync(await browser.GetAsync(new Uri("/account/login", UriKind.Relative)));
        using var _ = await browser.LogInAsync(loginPage, "deniz", "Correct-horse-9");

        _hasher.Verifications.ShouldBe(1);
    }

    private sealed class CountingPasswordHasher : IPasswordHasher<User>
    {
        private readonly PasswordHasher<User> _real = new();
        private int _verifications;

        public int Verifications => _verifications;

        public string HashPassword(User user, string password) => _real.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(User user, string hashedPassword, string providedPassword)
        {
            Interlocked.Increment(ref _verifications);
            return _real.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }
}
