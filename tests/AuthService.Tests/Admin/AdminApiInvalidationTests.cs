using System.Net;
using System.Net.Http.Headers;
using AuthService.Admin;
using AuthService.Persistence;
using AuthService.Roles;
using AuthService.Tests.Hosting;
using AuthService.Tests.Outbox;
using AuthService.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.Tests.Admin;

/// <summary>
/// AuthService listens to its own invalidation events like any client app, so taking auth.Admin away from a role
/// locks its admins out right away, not after the 5-minute cache of role operations.
/// </summary>
[Collection(UsesPostgresAndRabbitMq.Name)]
public sealed class AdminApiInvalidationTests(PostgresFixture postgres, RabbitMqFixture rabbitMq)
{
    [Fact]
    public async Task Taking_auth_Admin_away_from_the_role_locks_its_admins_out_right_away()
    {
        await using var app = await AuthServiceApp.CreateAsync(postgres, ("RabbitMq:Uri", rabbitMq.Uri.AbsoluteUri));
        var authSecret = await AdminSteps.BootstrapAsync(app);
        using var http = app.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await AdminSteps.SignInAsync(app, "ayse", AuthApplication.Key, authSecret));
        (await http.GetAsync(new Uri("/admin/api/me", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.OK);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var adminRole = await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Roles.SingleAsync(r => r.Name == "Admin");
            await scope.ServiceProvider.GetRequiredService<RoleEditor>().SetOperationsAsync(adminRole.Id, []);
        }

        // The app's clock never moves here, so only the event can make this change.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var response = await http.GetAsync(new Uri("/admin/api/me", UriKind.Relative));
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The admin kept access after the role lost auth.Admin.");
    }
}
