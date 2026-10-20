using System.Net;
using Auth.Contracts.V1;
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

/// <summary>Signing out of one application: its session ends, the browser is signed out of AuthService.</summary>
[Collection(UsesPostgres.Name)]
public sealed class SignOutTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Password = "Correct-horse-9";
    private static readonly Uri SignedOut = new("https://billing.example/signed-out");

    private AuthServiceApp _app = null!;
    private CreatedApplication _billing = null!;
    private Browser _browser = null!;

    public async Task InitializeAsync()
    {
        _app = await AuthServiceApp.CreateAsync(postgres);
        await using var scope = _app.Services.CreateAsyncScope();
        _billing = await scope.ServiceProvider.GetRequiredService<ApplicationService>()
            .CreateAsync(new NewApplication("billing", "Billing", [Browser.RedirectUri("billing")], [SignedOut]));

        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var user = new User { UserName = "deniz", DisplayName = "Deniz" };
        (await users.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();
        db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = _billing.Application.Id });
        var accountant = await db.AddRole(_billing.Application, "Accountant");
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = accountant.Id });
        await db.SaveChangesAsync();

        _browser = new Browser(_app);
    }

    public async Task DisposeAsync()
    {
        _browser.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Signing_out_ends_the_session_and_returns_to_the_app()
    {
        var (idToken, sessionId) = await SignIn();

        using var response = await _browser.GetAsync(EndSessionUri(idToken));

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.GetLeftPart(UriPartial.Path).ShouldBe(SignedOut.AbsoluteUri);
        Browser.QueryValue(response, "state").ShouldBe("bye");
        (await SessionEndedAt(sessionId)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Signing_out_tells_client_apps_that_the_session_stopped()
    {
        var (idToken, sessionId) = await SignIn();

        using var _ = await _browser.GetAsync(EndSessionUri(idToken));

        await using var scope = _app.Services.CreateAsyncScope();
        var message = await scope.ServiceProvider.GetRequiredService<AuthDbContext>().OutboxMessages.SingleAsync();
        InvalidationEvent.Parser.ParseFrom(message.Payload).SessionStopped.SessionId.ShouldBe(sessionId.ToString());
    }

    [Fact]
    public async Task After_signing_out_the_browser_must_log_in_again()
    {
        var (idToken, _) = await SignIn();
        using var _ = await _browser.GetAsync(EndSessionUri(idToken));

        using var next = await _browser.FollowAsync(await _browser.GetAsync(_browser.AuthorizeUri("billing")));

        next.RequestMessage!.RequestUri!.AbsolutePath.ShouldBe("/account/login");
    }

    [Fact]
    public async Task An_unregistered_post_logout_address_is_refused()
    {
        var (idToken, sessionId) = await SignIn();

        using var response = await _browser.GetAsync(new Uri(
            $"/connect/endsession?id_token_hint={idToken}&post_logout_redirect_uri={Uri.EscapeDataString("https://evil.example/")}",
            UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SessionEndedAt(sessionId)).ShouldBeNull("nothing happens on a refused request");
    }

    [Fact]
    public async Task Without_an_id_token_hint_nothing_happens()
    {
        // Otherwise any site could sign users out with a plain link (logout CSRF).
        var (_, sessionId) = await SignIn();

        using var response = await _browser.GetAsync(new Uri("/connect/endsession", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SessionEndedAt(sessionId)).ShouldBeNull();
        using var next = await _browser.FollowAsync(await _browser.GetAsync(_browser.AuthorizeUri("billing")));
        next.RequestMessage!.RequestUri!.AbsolutePath.ShouldNotBe("/account/login", "still signed in");
    }

    [Fact]
    public async Task Someone_elses_browser_cannot_end_the_session_with_a_leaked_id_token()
    {
        var (idToken, sessionId) = await SignIn();
        using var otherBrowser = new Browser(_app);

        using var _ = await otherBrowser.GetAsync(EndSessionUri(idToken));

        (await SessionEndedAt(sessionId)).ShouldBeNull();
    }

    [Fact]
    public async Task A_tampered_id_token_hint_is_refused()
    {
        var (idToken, sessionId) = await SignIn();
        var tampered = idToken[..^4] + (idToken[^4..] == "AAAA" ? "BBBB" : "AAAA");

        using var response = await _browser.GetAsync(EndSessionUri(tampered));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SessionEndedAt(sessionId)).ShouldBeNull();
    }

    [Fact]
    public async Task Signing_out_of_one_application_keeps_the_session_in_another()
    {
        var (billingIdToken, _) = await SignIn();
        var hrSessionId = await SignInToHrToo();

        using var _ = await _browser.GetAsync(EndSessionUri(billingIdToken));

        (await SessionEndedAt(hrSessionId)).ShouldBeNull("sessions of different applications are independent");
    }

    [Fact]
    public async Task Sign_out_also_works_as_a_form_post()
    {
        var (idToken, sessionId) = await SignIn();

        using var response = await _browser.PostEndSessionAsync(idToken, SignedOut);

        response.Headers.Location!.GetLeftPart(UriPartial.Path).ShouldBe(SignedOut.AbsoluteUri);
        (await SessionEndedAt(sessionId)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Without_a_return_address_the_user_sees_a_signed_out_page()
    {
        var (idToken, _) = await SignIn();

        using var response = await _browser.FollowAsync(await _browser.GetAsync(
            new Uri($"/connect/endsession?id_token_hint={idToken}", UriKind.Relative)));

        response.RequestMessage!.RequestUri!.AbsolutePath.ShouldBe("/account/signed-out");
        (await response.Content.ReadAsStringAsync()).ShouldContain("Çıkış yapıldı");
    }

    private async Task<Guid> SignInToHrToo()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var hr = await scope.ServiceProvider.GetRequiredService<ApplicationService>()
            .CreateAsync(new NewApplication("hr", "HR", [Browser.RedirectUri("hr")], []));
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var user = await db.Users.SingleAsync();
        db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = hr.Application.Id });
        var payroll = await db.AddRole(hr.Application, "Payroll");
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = payroll.Id });
        await db.SaveChangesAsync();

        // Already signed in to AuthService, so this goes straight to the code.
        var redirect = await _browser.FollowAsync(await _browser.GetAsync(_browser.AuthorizeUri("hr")));
        var (_, body) = await _browser.ExchangeCodeAsync("hr", hr.ClientSecret, Browser.CodeFrom(redirect));
        return Guid.Parse(new JsonWebToken(body.RootElement.GetProperty("access_token").GetString()).GetClaim("sid").Value);
    }

    private async Task<(string IdToken, Guid SessionId)> SignIn()
    {
        using var loginPage = await _browser.FollowAsync(await _browser.GetAsync(_browser.AuthorizeUri("billing")));
        var redirect = await _browser.FollowAsync(await _browser.LogInAsync(loginPage, "deniz", Password));
        var (_, body) = await _browser.ExchangeCodeAsync("billing", _billing.ClientSecret, Browser.CodeFrom(redirect));
        var idToken = body.RootElement.GetProperty("id_token").GetString()!;
        return (idToken, Guid.Parse(new JsonWebToken(idToken).GetClaim("sid").Value));
    }

    private static Uri EndSessionUri(string idToken) => new(
        $"/connect/endsession?id_token_hint={idToken}&post_logout_redirect_uri={Uri.EscapeDataString(SignedOut.AbsoluteUri)}&state=bye",
        UriKind.Relative);

    private async Task<DateTimeOffset?> SessionEndedAt(Guid sessionId)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Sessions
            .Where(s => s.Id == sessionId).Select(s => s.EndedAt).SingleAsync();
    }
}
