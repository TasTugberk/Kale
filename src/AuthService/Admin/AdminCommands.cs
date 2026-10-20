using System.ComponentModel.DataAnnotations;
using AuthService.Applications;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Roles;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Admin;

/// <summary>
/// `AuthService admin &lt;command&gt; ...`: one-off commands that set up an environment (local compose, a smoke
/// test) until there is an admin UI. They use the app's own services, so the same rules apply.
/// </summary>
public static class AdminCommands
{
    public const string Usage = """
        Usage: AuthService admin <command>
          create-application <key> <name> <redirect-uri> [<post-logout-redirect-uri>]
              Prints the new client secret. It is shown only this once.
          create-user <user-name> <display-name>
              Reads the password from the first line of standard input.
          create-role <application-key> <role-name>
          set-role-operations <application-key> <role-name> [<operation>...]
              Full operation names, e.g. sample.NoteRead (the application must have registered them).
          assign-role <user-name> <application-key> <role-name>
              Also gives the user active access to the application.
        """;

    /// <returns>0 when it worked, 1 when the command failed (the reason is on <paramref name="errors"/>), 2 for
    /// an unknown command or wrong arguments.</returns>
    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextReader input, TextWriter output, TextWriter errors)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        try
        {
            switch (args)
            {
                case ["create-application", var key, var name, var redirectUri]:
                    await CreateApplicationAsync(provider, key, name, redirectUri, postLogoutRedirectUri: null, output);
                    return 0;

                case ["create-application", var key, var name, var redirectUri, var postLogoutRedirectUri]:
                    await CreateApplicationAsync(provider, key, name, redirectUri, postLogoutRedirectUri, output);
                    return 0;

                case ["create-user", var userName, var displayName]:
                    await CreateUserAsync(provider, userName, displayName, await input.ReadLineAsync());
                    return 0;

                case ["create-role", var applicationKey, var roleName]:
                    await CreateRoleAsync(provider, applicationKey, roleName);
                    return 0;

                case ["set-role-operations", var applicationKey, var roleName, .. var operations]:
                    var role = await FindRoleAsync(provider.GetRequiredService<AuthDbContext>(), applicationKey, roleName);
                    await provider.GetRequiredService<RoleEditor>().SetOperationsAsync(role.Id, operations);
                    return 0;

                case ["assign-role", var userName, var applicationKey, var roleName]:
                    await AssignRoleAsync(provider, userName, applicationKey, roleName);
                    return 0;

                default:
                    await errors.WriteLineAsync(Usage);
                    return 2;
            }
        }
        catch (Exception exception) when (exception is ValidationException or InvalidOperationException or UriFormatException)
        {
            await errors.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private static async Task CreateApplicationAsync(
        IServiceProvider provider, string key, string name, string redirectUri, string? postLogoutRedirectUri, TextWriter output)
    {
        List<Uri> postLogoutRedirectUris = postLogoutRedirectUri is null ? [] : [new Uri(postLogoutRedirectUri)];
        var created = await provider.GetRequiredService<ApplicationService>()
            .CreateAsync(new NewApplication(key, name, [new Uri(redirectUri)], postLogoutRedirectUris));

        // Only the secret, so a script can pipe it straight into `podman secret create`.
        await output.WriteLineAsync(created.ClientSecret);
    }

    private static async Task CreateUserAsync(IServiceProvider provider, string userName, string displayName, string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException("Give the password on the first line of standard input.");
        }

        var user = new User { UserName = userName, DisplayName = displayName };
        var result = await provider.GetRequiredService<UserManager<User>>().CreateAsync(user, password);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join(" ", result.Errors.Select(e => e.Description)));
        }
    }

    private static async Task CreateRoleAsync(IServiceProvider provider, string applicationKey, string roleName)
    {
        var db = provider.GetRequiredService<AuthDbContext>();
        var application = await FindApplicationAsync(db, applicationKey);
        if (await db.Roles.AnyAsync(r => r.ApplicationId == application.Id && r.Name == roleName))
        {
            throw new InvalidOperationException($"Application '{applicationKey}' already has a role '{roleName}'.");
        }

        db.Roles.Add(new Role { ApplicationId = application.Id, Name = roleName });
        await db.SaveChangesAsync();
    }

    private static async Task AssignRoleAsync(IServiceProvider provider, string userName, string applicationKey, string roleName)
    {
        var db = provider.GetRequiredService<AuthDbContext>();
        var user = await db.Users.SingleOrDefaultAsync(u => u.UserName == userName)
            ?? throw new InvalidOperationException($"No user '{userName}'.");
        var role = await FindRoleAsync(db, applicationKey, roleName);

        // Sign-in needs an active access row as well as the role (CLAUDE.md decision).
        var access = await db.UserApplications.SingleOrDefaultAsync(a => a.UserId == user.Id && a.ApplicationId == role.ApplicationId);
        if (access is null)
        {
            db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = role.ApplicationId });
        }
        else
        {
            access.IsActive = true;
        }

        if (!await db.UserRoles.AnyAsync(ur => ur.UserId == user.Id && ur.RoleId == role.Id))
        {
            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        }

        await db.SaveChangesAsync();
    }

    private static async Task<Application> FindApplicationAsync(AuthDbContext db, string applicationKey) =>
        await db.Applications.SingleOrDefaultAsync(a => a.Key == applicationKey)
            ?? throw new InvalidOperationException($"No application '{applicationKey}'.");

    private static async Task<Role> FindRoleAsync(AuthDbContext db, string applicationKey, string roleName)
    {
        var application = await FindApplicationAsync(db, applicationKey);
        return await db.Roles.SingleOrDefaultAsync(r => r.ApplicationId == application.Id && r.Name == roleName)
            ?? throw new InvalidOperationException($"Application '{applicationKey}' has no role '{roleName}'.");
    }
}
