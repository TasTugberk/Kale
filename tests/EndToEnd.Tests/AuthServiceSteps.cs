using System.Text.Json;
using AuthService.Applications;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using AuthService.Tests.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EndToEnd.Tests;

/// <summary>What the end-to-end tests do on AuthService's side: sign users in, change data, wait for background work.</summary>
internal static class AuthServiceSteps
{
    public const string Password = "Correct-horse-9";

    /// <summary>
    /// Creates a user with access to <paramref name="application"/> and a role holding <paramref name="operation"/>
    /// (no operation when null), signs them in through AuthService's real flow, and returns the token response
    /// (access, id and refresh tokens).
    /// </summary>
    public static async Task<JsonElement> SignInAsync(AuthServiceApp auth, CreatedApplication application, string? operation)
    {
        var userName = $"user-{Guid.CreateVersion7():N}";
        await using (var scope = auth.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var user = new User { UserName = userName, DisplayName = userName };
            (await users.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();
            db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = application.Application.Id });
            var role = await db.AddRole(application.Application, $"Role-{Guid.CreateVersion7():N}");
            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
            if (operation is not null)
            {
                var stored = await db.Operations.SingleAsync(o => o.Name == operation);
                db.RoleOperations.Add(new RoleOperation { ApplicationId = application.Application.Id, RoleId = role.Id, OperationId = stored.Id });
            }

            await db.SaveChangesAsync();
        }

        var key = application.Application.Key;
        using var browser = new Browser(auth);
        using var loginPage = await browser.FollowAsync(await browser.GetAsync(browser.AuthorizeUri(key)));
        var redirect = await browser.FollowAsync(await browser.LogInAsync(loginPage, userName, Password));
        var (_, body) = await browser.ExchangeCodeAsync(key, application.ClientSecret, Browser.CodeFrom(redirect));
        return body.RootElement.Clone();
    }

    public static async Task WithAuthDbAsync(AuthServiceApp auth, Func<AuthDbContext, Task> action)
    {
        await using var scope = auth.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await action(db);
        await db.SaveChangesAsync();
    }

    /// <summary>Client apps register in the background after startup; waits (up to 10 s) until AuthService shows it.</summary>
    public static async Task WaitUntilAsync(AuthServiceApp auth, Func<AuthDbContext, Task<bool>> condition, Action? eachAttempt = null)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            eachAttempt?.Invoke();
            await using var scope = auth.Services.CreateAsyncScope();
            if (await condition(scope.ServiceProvider.GetRequiredService<AuthDbContext>()))
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("AuthService never showed the expected registration.");
    }
}
