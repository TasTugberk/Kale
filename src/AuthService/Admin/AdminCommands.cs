using System.ComponentModel.DataAnnotations;
using AuthService.Applications;
using AuthService.Domain;
using AuthService.Operations;
using AuthService.Persistence;
using AuthService.Roles;
using AuthService.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AuthService.Admin;

/// <summary>
/// `AuthService admin &lt;command&gt; ...`: one-off commands that set up an environment (local compose, a smoke
/// test) until there is an admin UI. They use the app's own services, so the same rules apply.
/// </summary>
public static class AdminCommands
{
    public const string Usage = """
        Usage: AuthService admin <command>
          bootstrap <user-name> <display-name> <admin-ui-redirect-uri> [<admin-ui-post-logout-redirect-uri>]
              First setup: the application "auth" (the admin UI's client) with auth.Admin, its role Admin, and the
              first admin user (password from the first line of standard input). Prints the admin UI's client
              secret, shown only this once.
          migrate
              Brings the database up to date (applies the migrations it doesn't have yet).
          create-application <key> <name> [<redirect-uri> [<post-logout-redirect-uri>]]
              Prints the new client secret. It is shown only this once. Without addresses: an application with
              only an API (its users can't sign in to it directly).
          rotate-secret <key> [--retire-old]
              Prints a new client secret for the application. The old one keeps working for a while
              (Applications:OldSecretValidFor, default 7 days) so running instances can be redeployed; only one
              old secret is kept. --retire-old stops the old one at once (e.g. after a leak).
          retire-old-secret <key>
              Stops the old secret at once, once every instance runs with the new one.
          desktop-sign-in <key> on|off
              Lets desktop apps sign users in to the application (client id <key>.desktop: no secret, the system
              browser and a loopback address), or stops it.
          delete-application <key>
              Deletes a deactivated application (deactivate it in the admin UI first) with its roles, operations,
              access rows and clients; its sessions stop. Users and groups stay. "auth" can't be deleted.
          create-user <user-name> <display-name>
              Reads the password from the first line of standard input.
          set-password <user-name>
              A new password (from the first line of standard input). Ends a lockout and stops the user's sessions
              in every application: the way back for an admin who is locked out or forgot their password.
          rename-user <user-name> <new-user-name> <new-display-name>
              Corrects the user's names. They stay signed in.
          delete-user <user-name>
              Deletes the user with their access, roles, group memberships and sessions (stopped first). The last
              administrator can't be deleted.
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
                case ["bootstrap", var userName, var displayName, var redirectUri]:
                    await BootstrapAsync(provider, userName, displayName, redirectUri, null, await input.ReadLineAsync(), output);
                    return 0;

                case ["bootstrap", var userName, var displayName, var redirectUri, var postLogoutRedirectUri]:
                    await BootstrapAsync(provider, userName, displayName, redirectUri, postLogoutRedirectUri, await input.ReadLineAsync(), output);
                    return 0;

                case ["create-application", var key, var name]:
                    await CreateApplicationAsync(provider, key, name, redirectUri: null, postLogoutRedirectUri: null, output);
                    return 0;

                case ["create-application", var key, var name, var redirectUri]:
                    await CreateApplicationAsync(provider, key, name, redirectUri, postLogoutRedirectUri: null, output);
                    return 0;

                case ["create-application", var key, var name, var redirectUri, var postLogoutRedirectUri]:
                    await CreateApplicationAsync(provider, key, name, redirectUri, postLogoutRedirectUri, output);
                    return 0;

                case ["rotate-secret", var key]:
                    await RotateSecretAsync(provider, key, retireOldSecret: false, output, errors);
                    return 0;

                case ["rotate-secret", var key, "--retire-old"]:
                    await RotateSecretAsync(provider, key, retireOldSecret: true, output, errors);
                    return 0;

                case ["retire-old-secret", var key]:
                    await RetireOldSecretAsync(provider, key);
                    return 0;

                case ["desktop-sign-in", var key, "on"]:
                    await SetDesktopSignInAsync(provider, key, enabled: true);
                    return 0;

                case ["desktop-sign-in", var key, "off"]:
                    await SetDesktopSignInAsync(provider, key, enabled: false);
                    return 0;

                case ["delete-application", var key]:
                    var application = await FindApplicationAsync(provider.GetRequiredService<AuthDbContext>(), key);
                    await provider.GetRequiredService<ApplicationService>().DeleteAsync(application.Id);
                    return 0;

                case ["create-user", var userName, var displayName]:
                    var password = RequirePassword(await input.ReadLineAsync());
                    await provider.GetRequiredService<UserCreator>().CreateAsync(userName, displayName, password);
                    return 0;

                case ["set-password", var userName]:
                    await SetPasswordAsync(provider, userName, RequirePassword(await input.ReadLineAsync()));
                    return 0;

                case ["rename-user", var userName, var newUserName, var newDisplayName]:
                    var renamed = await FindUserAsync(provider, userName);
                    await provider.GetRequiredService<UserEditor>().UpdateAsync(renamed.Id, newUserName, newDisplayName);
                    return 0;

                case ["delete-user", var userName]:
                    var deleted = await FindUserAsync(provider, userName);
                    await provider.GetRequiredService<UserEditor>().DeleteAsync(deleted.Id, deletedBy: null);
                    return 0;

                case ["create-role", var applicationKey, var roleName]:
                    await CreateRoleAsync(provider, applicationKey, roleName);
                    return 0;

                case ["set-role-operations", var applicationKey, var roleName, .. var operations]:
                    await SetRoleOperationsAsync(provider, applicationKey, roleName, operations);
                    return 0;

                case ["assign-role", var userName, var applicationKey, var roleName]:
                    await AssignRoleAsync(provider, userName, applicationKey, roleName);
                    return 0;

                default:
                    await errors.WriteLineAsync(Usage);
                    return 2;
            }
        }
        // The database's own errors (e.g. a wrong password) are NpgsqlExceptions; an unreachable database comes as an
        // InvalidOperationException ("likely due to a transient failure") after EF Core's retries.
        catch (Exception exception) when (exception is ValidationException or InvalidOperationException or UriFormatException or NpgsqlException)
        {
            await errors.WriteLineAsync(exception.Message);
            return 1;
        }
        catch (DbUpdateException exception)
        {
            // E.g. a value too long for its column, or another run changing the same rows at the same time. The
            // database's own message (the inner exception) has its error code and, for a constraint, its name.
            await errors.WriteLineAsync(exception.InnerException?.Message ?? exception.Message);
            return 1;
        }
    }

    /// <summary>
    /// `admin migrate`: brings the database up to date. Program.cs runs it before setting up the rest of the app,
    /// so a one-shot migration needs only the connection string, not the issuer, broker or key certificate (nor
    /// their secrets).
    /// </summary>
    public static async Task<int> MigrateAsync(string connectionString, TextWriter errors)
    {
        try
        {
            await using var db = new AuthDbContext(AuthDatabaseOptions.Create(connectionString), TimeProvider.System);
            await db.Database.MigrateAsync();
            return 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or NpgsqlException)
        {
            await errors.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private static async Task BootstrapAsync(IServiceProvider provider, string userName, string displayName,
        string redirectUri, string? postLogoutRedirectUri, string? password, TextWriter output)
    {
        var db = provider.GetRequiredService<AuthDbContext>();
        if (await db.Applications.AnyAsync(a => a.Key == AuthApplication.Key))
        {
            throw new InvalidOperationException($"The application '{AuthApplication.Key}' already exists: bootstrap runs once.");
        }

        // Everything that can be refused is checked before anything is written, so a failed bootstrap leaves
        // nothing behind and can simply be run again with corrected input.
        List<Uri> postLogoutRedirectUris = postLogoutRedirectUri is null ? [] : [new Uri(postLogoutRedirectUri)];
        var application = new NewApplication(AuthApplication.Key, "Kale Admin", [new Uri(redirectUri)], postLogoutRedirectUris);
        var applications = provider.GetRequiredService<ApplicationService>();
        applications.Validate(application);
        var creator = provider.GetRequiredService<UserCreator>();
        await creator.CheckAsync(userName, displayName, RequirePassword(password));

        await creator.CreateAsync(userName, displayName, password!);
        var created = await applications.CreateAsync(application);

        // AuthService's own operations, registered the way a client app registers its enum.
        var catalog = Auth.Client.OperationCatalog.For<AuthOperations>(AuthApplication.Key);
        var declarations = catalog.Declarations.Select(d => new OperationDeclaration(d.Name, d.Implies)).ToList();
        await provider.GetRequiredService<OperationRegistrar>().RegisterAsync(created.Application.Id, declarations);

        const string adminRole = "Admin";
        await CreateRoleAsync(provider, AuthApplication.Key, adminRole);
        await SetRoleOperationsAsync(provider, AuthApplication.Key, adminRole, [catalog.QualifiedName(nameof(AuthOperations.Admin))]);
        await AssignRoleAsync(provider, userName, AuthApplication.Key, adminRole);

        await output.WriteLineAsync(created.ClientSecret);
    }

    private static async Task CreateApplicationAsync(
        IServiceProvider provider, string key, string name, string? redirectUri, string? postLogoutRedirectUri, TextWriter output)
    {
        List<Uri> redirectUris = redirectUri is null ? [] : [new Uri(redirectUri)];
        List<Uri> postLogoutRedirectUris = postLogoutRedirectUri is null ? [] : [new Uri(postLogoutRedirectUri)];
        var created = await provider.GetRequiredService<ApplicationService>()
            .CreateAsync(new NewApplication(key, name, redirectUris, postLogoutRedirectUris));

        // Only the secret, so a script can pipe it straight into `podman secret create`.
        await output.WriteLineAsync(created.ClientSecret);
    }

    private static async Task RotateSecretAsync(
        IServiceProvider provider, string key, bool retireOldSecret, TextWriter output, TextWriter errors)
    {
        var application = await FindApplicationAsync(provider.GetRequiredService<AuthDbContext>(), key);
        var rotated = await provider.GetRequiredService<ApplicationService>().RotateSecretAsync(application.Id, retireOldSecret);

        // Only the secret on standard output, like create-application; the note goes to standard error.
        await output.WriteLineAsync(rotated.ClientSecret);
        await errors.WriteLineAsync(rotated.OldSecretValidUntil is { } validUntil
            ? $"The old secret still works until {validUntil:u} (or until retire-old-secret {key})."
            : "The old secret no longer works.");
    }

    private static async Task RetireOldSecretAsync(IServiceProvider provider, string key)
    {
        var application = await FindApplicationAsync(provider.GetRequiredService<AuthDbContext>(), key);
        await provider.GetRequiredService<ApplicationService>().RetireOldSecretAsync(application.Id);
    }

    private static async Task SetDesktopSignInAsync(IServiceProvider provider, string key, bool enabled)
    {
        var application = await FindApplicationAsync(provider.GetRequiredService<AuthDbContext>(), key);
        await provider.GetRequiredService<ApplicationService>().SetDesktopSignInAsync(application.Id, enabled);
    }

    private static async Task SetPasswordAsync(IServiceProvider provider, string userName, string password)
    {
        var user = await FindUserAsync(provider, userName);
        await provider.GetRequiredService<UserPasswords>().SetAsync(user.Id, password);
    }

    // By Identity's normalized name, as the login page finds users: a locked-out admin typing "Ayse" for "ayse"
    // gets in.
    private static async Task<User> FindUserAsync(IServiceProvider provider, string userName) =>
        await provider.GetRequiredService<UserManager<User>>().FindByNameAsync(userName)
            ?? throw new InvalidOperationException($"No user '{userName}'.");

    // The password comes from standard input; without one there's nothing to check.
    private static string RequirePassword(string? password) =>
        string.IsNullOrEmpty(password)
            ? throw new InvalidOperationException("Give the password on the first line of standard input.")
            : password;

    private static async Task CreateRoleAsync(IServiceProvider provider, string applicationKey, string roleName)
    {
        var application = await FindApplicationAsync(provider.GetRequiredService<AuthDbContext>(), applicationKey);
        await provider.GetRequiredService<RoleEditor>().CreateAsync(application.Id, roleName, description: null);
    }

    private static async Task SetRoleOperationsAsync(IServiceProvider provider, string applicationKey, string roleName, string[] operations)
    {
        var role = await FindRoleAsync(provider.GetRequiredService<AuthDbContext>(), applicationKey, roleName);
        await provider.GetRequiredService<RoleEditor>().SetOperationsAsync(role.Id, operations);
    }

    private static async Task AssignRoleAsync(IServiceProvider provider, string userName, string applicationKey, string roleName)
    {
        var db = provider.GetRequiredService<AuthDbContext>();
        var user = await db.Users.SingleOrDefaultAsync(u => u.UserName == userName)
            ?? throw new InvalidOperationException($"No user '{userName}'.");
        var role = await FindRoleAsync(db, applicationKey, roleName);
        var userAccess = provider.GetRequiredService<UserAccess>();
        await userAccess.AssignRoleAsync(user.Id, role.Id);

        // Unlike the admin API, this also turns access that was turned off back on (and gives it to a user who
        // already held the role): it's the way back in for an administrator who lost it (handover, "Recovery").
        var access = await db.UserApplications.AsNoTracking()
            .SingleOrDefaultAsync(a => a.UserId == user.Id && a.ApplicationId == role.ApplicationId);
        if (access is not { IsActive: true })
        {
            await userAccess.SetApplicationAccessAsync(user.Id, role.ApplicationId, isActive: true, access?.DefaultRoleId);
        }
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
