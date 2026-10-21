using System.ComponentModel.DataAnnotations;
using AuthService.Domain;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using AuthService.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.Tests.Users;

/// <summary>Creating a user: Identity's rules (unique valid name, strong password) plus a display name.</summary>
[Collection(UsesPostgres.Name)]
public sealed class UserCreatorTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Password = "Correct-horse-9";

    private AuthServiceApp _app = null!;
    private AsyncServiceScope _scope;
    private UserCreator _creator = null!;

    public async Task InitializeAsync()
    {
        _app = await AuthServiceApp.CreateAsync(postgres);
        _scope = _app.Services.CreateAsyncScope();
        _creator = _scope.ServiceProvider.GetRequiredService<UserCreator>();
    }

    public async Task DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task A_user_is_created_with_a_hashed_password()
    {
        var user = await _creator.CreateAsync("deniz", "Deniz Yılmaz", Password);

        user.DisplayName.ShouldBe("Deniz Yılmaz");
        user.IsActive.ShouldBeTrue();
        var users = _scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        (await users.CheckPasswordAsync((await users.FindByNameAsync("deniz"))!, Password)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("deniz", "Other", Password, "already taken")]
    [InlineData("ayse", "Ayşe", "short", "12")]
    [InlineData("ayse", "Ayşe", "", "password")]
    [InlineData("ayse", " ", Password, "display name")]
    public async Task Invalid_input_is_refused_with_the_reason(string userName, string displayName, string password, string reason)
    {
        await _creator.CreateAsync("deniz", "Deniz", Password);

        var error = await Should.ThrowAsync<ValidationException>(() => _creator.CreateAsync(userName, displayName, password));

        error.Message.ShouldContain(reason);
    }

    [Fact]
    public async Task A_display_name_has_a_limit()
    {
        var error = await Should.ThrowAsync<ValidationException>(() => _creator.CreateAsync("ayse", new string('a', 201), Password));

        error.Message.ShouldContain("200");
    }
}
