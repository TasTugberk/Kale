using System.Diagnostics;
using System.Net;
using AuthService.Admin;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenIddict.Abstractions;

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

        // The secret works: the application can get a client-credentials token with it.
        (await ClientCredentials("sample", result.Output.Trim())).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Create_application_without_addresses_is_for_an_application_that_only_has_an_API()
    {
        var result = await Run("", "create-application", "orders", "Orders");

        result.ExitCode.ShouldBe(0, result.Errors);
        (await ClientCredentials("orders", result.Output.Trim())).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Rotate_secret_prints_a_new_secret_and_the_old_one_stops_working()
    {
        var oldSecret = (await Run("", "create-application", "orders", "Orders")).Output.Trim();

        var result = await Run("", "rotate-secret", "orders");

        result.ExitCode.ShouldBe(0, result.Errors);
        var newSecret = result.Output.Trim();
        newSecret.ShouldNotBe(oldSecret);
        (await ClientCredentials("orders", newSecret)).Status.ShouldBe(HttpStatusCode.OK);
        (await ClientCredentials("orders", oldSecret)).ShouldBe((HttpStatusCode.Unauthorized, "invalid_client"));
    }

    [Fact]
    public async Task Rotate_secret_of_an_unknown_application_fails_with_exit_code_1()
    {
        var result = await Run("", "rotate-secret", "nope");

        result.ExitCode.ShouldBe(1);
        result.Errors.ShouldContain("nope");
    }

    // A client-credentials token request with this secret: the status, and the OAuth error when refused.
    private async Task<(HttpStatusCode Status, string? Error)> ClientCredentials(string clientId, string secret)
    {
        using var http = _app.CreateClient();
        using var token = await http.PostAsync(new Uri("/connect/token", UriKind.Relative), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
        }));
        using var body = System.Text.Json.JsonDocument.Parse(await token.Content.ReadAsStringAsync());
        var error = body.RootElement.TryGetProperty("error", out var value) ? value.GetString() : null;
        return (token.StatusCode, error);
    }

    [Fact]
    public async Task Through_the_real_program_standard_output_is_only_the_secret()
    {
        // As a script runs it: `dotnet AuthService.dll admin create-application ... | podman secret create ...`.
        // Log lines (EF Core logs every SQL command) must not end up in the secret.
        var (exitCode, output, errors) = await RunProgram(_app.ConnectionString,
            "create-application", "sample", "Sample App", "http://localhost:8082/signin-oidc");

        exitCode.ShouldBe(0, errors);
        output.TrimEnd('\n').ShouldNotContain('\n', "only one line: the secret");
        await using var scope = _app.Services.CreateAsyncScope();
        var clients = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var client = await clients.FindByClientIdAsync("sample");
        (await clients.ValidateClientSecretAsync(client!, output.Trim())).ShouldBeTrue();
    }

    [Fact]
    public async Task Through_the_real_program_an_unreachable_database_is_reported_with_exit_code_1()
    {
        var (exitCode, _, errors) = await RunProgram("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x",
            "create-role", "sample", "Reader");

        exitCode.ShouldBe(1);
        errors.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Migrate_brings_a_new_database_up_to_date()
    {
        // Compose runs this once before starting AuthService. Through the real program: the web app itself
        // can't start on an empty database (it loads its keys from it).
        await using var empty = postgres.CreateDbContext();
        var connectionString = postgres.ConnectionString(empty.Database.GetDbConnection().Database);

        // Production, with nothing but the connection string: a one-shot migration needs no issuer, broker or key
        // certificate (nor their secrets).
        var (exitCode, _, errors) = await RunProgram(connectionString, ["migrate"], environment: "Production");

        exitCode.ShouldBe(0, errors);
        (await empty.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("migrate")]
    [InlineData("create-role", "sample", "Reader")]
    public async Task A_wrong_database_password_is_reported_with_exit_code_1(params string[] command)
    {
        var wrongPassword = new NpgsqlConnectionStringBuilder(_app.ConnectionString) { Password = "wrong" }.ConnectionString;

        var (exitCode, _, errors) = await RunProgram(wrongPassword, command);

        exitCode.ShouldBe(1);
        errors.ShouldContain("28P01"); // PostgreSQL: invalid_password
    }

    [Fact]
    public async Task Create_application_can_also_register_a_post_logout_address()
    {
        var result = await Run("", "create-application", "sample", "Sample App",
            "http://localhost:8082/signin-oidc", "http://localhost:8082/signout-callback-oidc");

        result.ExitCode.ShouldBe(0, result.Errors);
        await using var scope = _app.Services.CreateAsyncScope();
        var clients = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var client = await clients.FindByClientIdAsync("sample");
        (await clients.GetPostLogoutRedirectUrisAsync(client!)).ShouldBe(["http://localhost:8082/signout-callback-oidc"]);
    }

    [Theory]
    [InlineData("Correct-horse-9", "http://admin.example/signin-oidc")] // plain http outside localhost
    [InlineData("short", "https://admin.example/signin-oidc")]          // a password Identity refuses
    public async Task A_bootstrap_that_fails_leaves_nothing_behind_so_a_corrected_run_works(string password, string redirectUri)
    {
        var failed = await Run(password + "\n", "bootstrap", "ayse", "Ayşe", redirectUri);
        failed.ExitCode.ShouldBe(1);

        var corrected = await Run(Password + "\n", "bootstrap", "ayse", "Ayşe", "https://admin.example/signin-oidc");

        corrected.ExitCode.ShouldBe(0, corrected.Errors);
    }

    [Fact]
    public async Task Bootstrap_can_register_the_admin_UIs_post_logout_address()
    {
        var result = await Run(Password + "\n", "bootstrap", "ayse", "Ayşe",
            "https://admin.example/signin-oidc", "https://admin.example/signed-out");

        result.ExitCode.ShouldBe(0, result.Errors);
        await using var scope = _app.Services.CreateAsyncScope();
        var clients = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        (await clients.GetPostLogoutRedirectUrisAsync((await clients.FindByClientIdAsync("auth"))!)).ShouldBe(["https://admin.example/signed-out"]);
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
    public async Task Assigning_a_role_turns_disabled_access_back_on()
    {
        await SetUpSampleWithReader();
        await Run("", "assign-role", "deniz", "sample", "Reader");
        await WithDb(async db =>
        {
            (await db.UserApplications.SingleAsync()).IsActive = false;
            await db.SaveChangesAsync();
        });

        (await Run("", "assign-role", "deniz", "sample", "Reader")).ExitCode.ShouldBe(0);

        await WithDb(async db => (await db.UserApplications.SingleAsync()).IsActive.ShouldBeTrue());
    }

    [Theory]
    [InlineData("", "create-role", "nope", "Reader", "nope")]
    [InlineData("", "create-role", "sample", "Reader", "already has a role")]
    [InlineData("", "assign-role", "nobody", "sample", "Reader", "nobody")]
    [InlineData("", "create-user", "ayse", "Ayşe", "standard input")]
    [InlineData("short\n", "create-user", "ayse", "Ayşe", "12")]
    public async Task A_command_that_fails_says_why_and_exits_with_1(string input, params string[] argsThenReason)
    {
        await SetUpSampleWithReader();

        var result = await Run(input, argsThenReason[..^1]);

        result.ExitCode.ShouldBe(1);
        result.Errors.ShouldContain(argsThenReason[^1]);
    }

    [Fact]
    public async Task A_database_error_is_reported_like_any_other_failure()
    {
        await SetUpSampleWithReader();

        // Application names are limited to 200 characters by the database (nothing checks that before it).
        var result = await Run("", "create-application", "long-name", new string('x', 201), "https://long.example/signin-oidc");

        result.ExitCode.ShouldBe(1);
        result.Errors.ShouldContain("22001"); // PostgreSQL: string_data_right_truncation
    }

    [Fact]
    public async Task An_unknown_command_prints_the_usage_and_exits_with_2()
    {
        var result = await Run("", "make-coffee");

        result.ExitCode.ShouldBe(2);
        result.Errors.ShouldContain("create-application");
    }

    // The built AuthService.dll as a separate process, as a script runs it. Both outputs are read at the same
    // time (a full stderr pipe would otherwise block the process), and a hung process fails the test.
    private static Task<(int ExitCode, string Output, string Errors)> RunProgram(string connectionString, params string[] adminArgs) =>
        RunProgram(connectionString, adminArgs, environment: "Testing");

    private static async Task<(int ExitCode, string Output, string Errors)> RunProgram(
        string connectionString, string[] adminArgs, string environment)
    {
        var start = new ProcessStartInfo("dotnet", [typeof(AdminCommands).Assembly.Location, "admin", .. adminArgs])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Environment =
            {
                ["ASPNETCORE_ENVIRONMENT"] = environment,
                ["ConnectionStrings__AuthDb"] = connectionString,
            },
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output, await errors);
    }

    // Application "sample" with a role "Reader", and a user "deniz".
    private async Task SetUpSampleWithReader()
    {
        (await Run("", "create-application", "sample", "Sample App", "http://localhost:8082/signin-oidc")).ExitCode.ShouldBe(0);
        (await Run("", "create-role", "sample", "Reader")).ExitCode.ShouldBe(0);
        (await Run(Password + "\n", "create-user", "deniz", "Deniz")).ExitCode.ShouldBe(0);
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
