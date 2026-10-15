using System.Net;
using AuthService.Applications;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AuthService.Tests.SignIn;

/// <summary>DESIGN.md sign-in step 3: a user with several eligible roles and no usable default picks one.</summary>
[Collection(UsesPostgres.Name)]
public sealed class RolePickerTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Password = "Correct-horse-9";

    private AuthServiceApp _app = null!;
    private CreatedApplication _billing = null!;
    private Browser _browser = null!;
    private Guid _accountantId;
    private Guid _auditorId;

    public async Task InitializeAsync()
    {
        _app = await AuthServiceApp.CreateAsync(postgres);
        await using var scope = _app.Services.CreateAsyncScope();
        _billing = await scope.ServiceProvider.GetRequiredService<ApplicationService>()
            .CreateAsync(new NewApplication("billing", "Billing", [Browser.RedirectUri("billing")], []));

        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var user = new User { UserName = "deniz", DisplayName = "Deniz" };
        (await users.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();
        db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = _billing.Application.Id });
        var accountant = await db.AddRole(_billing.Application, "Accountant");
        var auditor = await db.AddRole(_billing.Application, "Auditor");
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = accountant.Id });
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = auditor.Id });
        await db.SaveChangesAsync();
        (_accountantId, _auditorId) = (accountant.Id, auditor.Id);

        _browser = new Browser(_app);
    }

    public async Task DisposeAsync()
    {
        _browser.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task A_user_with_several_roles_picks_one_and_the_session_uses_it()
    {
        using var picker = await SignInAndReachPicker();

        (await Browser.RoleChoicesAsync(picker)).ShouldBe([_accountantId, _auditorId], ignoreOrder: true);
        var redirect = await _browser.FollowAsync(await _browser.ChooseRoleAsync(picker, _auditorId));

        (await SessionRoleAfterExchange(redirect)).ShouldBe(_auditorId);
    }

    [Fact]
    public async Task A_role_the_user_does_not_hold_cannot_be_chosen()
    {
        using var picker = await SignInAndReachPicker();

        // A tampered form: the authorize endpoint re-checks the choice and shows the picker again.
        using var response = await _browser.FollowAsync(await _browser.ChooseRoleAsync(picker, Guid.CreateVersion7()));

        response.RequestMessage!.RequestUri!.AbsolutePath.ShouldBe("/account/select-role");
    }

    [Fact]
    public async Task A_client_app_can_ask_for_a_specific_role_to_switch_roles()
    {
        await SignInAndReachPicker();

        // Already signed in; the app asks for Accountant directly (e.g. a "switch role" menu).
        var redirect = await _browser.FollowAsync(await _browser.GetAsync(_browser.AuthorizeUri("billing", $"&kale_role={_accountantId}")));

        (await SessionRoleAfterExchange(redirect)).ShouldBe(_accountantId);
    }

    [Fact]
    public async Task The_role_picker_speaks_Turkish_or_English()
    {
        using var picker = await SignInAndReachPicker();
        (await picker.Content.ReadAsStringAsync()).ShouldContain("Rol seçin");

        using var english = new Browser(_app, acceptLanguage: "en");
        using var loginPage = await english.FollowAsync(await english.GetAsync(english.AuthorizeUri("billing")));
        using var englishPicker = await english.FollowAsync(await english.LogInAsync(loginPage, "deniz", Password));
        (await englishPicker.Content.ReadAsStringAsync()).ShouldContain("Choose a role");
    }

    [Fact]
    public async Task The_role_picker_needs_a_signed_in_user()
    {
        using var anonymous = new Browser(_app);

        using var response = await anonymous.FollowAsync(await anonymous.GetAsync(
            new Uri("/account/select-role?ReturnUrl=%2Fconnect%2Fauthorize%3Fclient_id%3Dbilling", UriKind.Relative)));

        response.RequestMessage!.RequestUri!.AbsolutePath.ShouldBe("/account/login");
    }

    [Fact]
    public async Task The_role_picker_only_returns_to_the_authorize_endpoint()
    {
        await SignInAndReachPicker();

        using var response = await _browser.GetAsync(
            new Uri("/account/select-role?ReturnUrl=https%3A%2F%2Fevil.example%2F", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private async Task<HttpResponseMessage> SignInAndReachPicker()
    {
        using var loginPage = await _browser.FollowAsync(await _browser.GetAsync(_browser.AuthorizeUri("billing")));
        var picker = await _browser.FollowAsync(await _browser.LogInAsync(loginPage, "deniz", Password));
        picker.RequestMessage!.RequestUri!.AbsolutePath.ShouldBe("/account/select-role");
        return picker;
    }

    private async Task<Guid> SessionRoleAfterExchange(HttpResponseMessage redirectToClient)
    {
        var (_, body) = await _browser.ExchangeCodeAsync("billing", _billing.ClientSecret, Browser.CodeFrom(redirectToClient));
        var sid = Guid.Parse(new JsonWebToken(body.RootElement.GetProperty("access_token").GetString()).GetClaim("sid").Value);
        await using var scope = _app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Sessions
            .Where(s => s.Id == sid).Select(s => s.RoleId).SingleAsync();
    }
}
