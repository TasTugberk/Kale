using System.Net;
using AuthService.Admin;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.Tests.Admin;

/// <summary>
/// `AuthService admin ...`: one-off commands that set up an environment (local compose, a smoke test) before
/// there is an admin UI. Run against the real app's services and database.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class AdminCommandsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Password = "Correct-horse-9";

    private AuthServiceApp _app = null!;

    public async Task InitializeAsync() => _app = await AuthServiceApp.CreateAsync(postgres);

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Create_application_prints_only_the_new_client_secret()
    {
        var result = await Run("", "create-application", "sample", "Sample App", "http://localhost:8082/signin-oidc");

        result.ExitCode.ShouldBe(0, result.Errors);
        var secret = result.Output.Trim();

        // The secret works: the application can get a client-credentials token with it.
        using var http = _app.CreateClient();
        using var token = await http.PostAsync(new Uri("/connect/token", UriKind.Relative), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "sample",
            ["client_secret"] = secret,
        }));
        token.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Create_user_reads_the_password_from_standard_input()
    {
        var result = await Run(Password + "\n", "create-user", "deniz", "Deniz Yılmaz");

        result.ExitCode.ShouldBe(0, result.Errors);
        result.Output.ShouldNotContain(Password);
        await using var scope = _app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await users.FindByNameAsync("deniz");
        user!.DisplayName.ShouldBe("Deniz Yılmaz");
        (await users.CheckPasswordAsync(user, Password)).ShouldBeTrue();
    }

    [Fact]
    public async Task Roles_get_operations_and_users_who_then_have_access()
    {
        (await Run("", "create-application", "sample", "Sample App", "http://localhost:8082/signin-oidc")).ExitCode.ShouldBe(0);
        await WithDb(async db => await db.AddOperation(await db.Applications.SingleAsync(), "NoteRead"));
        (await Run(Password + "\n", "create-user", "deniz", "Deniz")).ExitCode.ShouldBe(0);

        var role = await Run("", "create-role", "sample", "Reader");
        var operations = await Run("", "set-role-operations", "sample", "Reader", "sample.NoteRead");
        var assign = await Run("", "assign-role", "deniz", "sample", "Reader");

        role.ExitCode.ShouldBe(0, role.Errors);
        operations.ExitCode.ShouldBe(0, operations.Errors);
        assign.ExitCode.ShouldBe(0, assign.Errors);
        await WithDb(async db =>
        {
            var user = await db.Users.SingleAsync(u => u.UserName == "deniz");
            var reader = await db.Roles.SingleAsync(r => r.Name == "Reader");
            (await db.UserApplications.SingleAsync(a => a.UserId == user.Id)).IsActive.ShouldBeTrue();
            (await db.UserRoles.CountAsync(ur => ur.UserId == user.Id && ur.RoleId == reader.Id)).ShouldBe(1);
            (await db.RoleOperations.CountAsync(ro => ro.RoleId == reader.Id)).ShouldBe(1);
        });
    }

    [Fact]
    public async Task A_command_that_fails_says_why_and_exits_with_1()
    {
        var result = await Run("", "create-role", "nope", "Reader");

        result.ExitCode.ShouldBe(1);
        result.Errors.ShouldContain("nope");
    }

    [Fact]
    public async Task An_unknown_command_prints_the_usage_and_exits_with_2()
    {
        var result = await Run("", "make-coffee");

        result.ExitCode.ShouldBe(2);
        result.Errors.ShouldContain("create-application");
    }

    private async Task<(int ExitCode, string Output, string Errors)> Run(string input, params string[] args)
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var exitCode = await AdminCommands.RunAsync(_app.Services, args, new StringReader(input), output, errors);
        return (exitCode, output.ToString(), errors.ToString());
    }

    private async Task WithDb(Func<AuthDbContext, Task> action)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }
}
