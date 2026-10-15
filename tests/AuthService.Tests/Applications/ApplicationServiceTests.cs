using System.ComponentModel.DataAnnotations;
using AuthService.Applications;
using AuthService.Persistence;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthService.Tests.Applications;

/// <summary>
/// One Application ↔ one OpenIddict client, created and changed together (docs/design/phase-3-openiddict.md).
/// Runs against the real app's services, so OpenIddict's own application manager is used.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class ApplicationServiceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Uri BillingCallback = new("https://billing.example/signin-oidc");

    private AuthServiceApp _app = null!;
    private AsyncServiceScope _scope;
    private ApplicationService _applications = null!;
    private IOpenIddictApplicationManager _clients = null!;
    private AuthDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _app = await AuthServiceApp.StartAsync(postgres);
        _scope = _app.Services.CreateAsyncScope();
        _applications = _scope.ServiceProvider.GetRequiredService<ApplicationService>();
        _clients = _scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        _db = _scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    }

    public async Task DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Creating_an_application_creates_its_client_with_the_key_as_client_id()
    {
        var created = await _applications.CreateAsync(Billing());

        created.Application.Key.ShouldBe("billing");
        var client = await _clients.FindByClientIdAsync("billing");
        client.ShouldNotBeNull();
        (await _clients.GetClientTypeAsync(client)).ShouldBe(ClientTypes.Confidential);
        (await _clients.GetRedirectUrisAsync(client)).ShouldBe([BillingCallback.AbsoluteUri]);
    }

    [Fact]
    public async Task The_secret_is_returned_once_and_only_its_hash_is_stored()
    {
        var created = await _applications.CreateAsync(Billing());

        created.ClientSecret.Length.ShouldBeGreaterThanOrEqualTo(43, "32 random bytes, base64url");
        var client = (await _clients.FindByClientIdAsync("billing"))!;
        (await _clients.ValidateClientSecretAsync(client, created.ClientSecret)).ShouldBeTrue();

        var stored = await _db.Set<OpenIddictEntityFrameworkCoreApplication<Guid>>().Select(a => a.ClientSecret).SingleAsync();
        stored.ShouldNotBe(created.ClientSecret);
        stored!.ShouldNotContain(created.ClientSecret);
    }

    [Fact]
    public async Task The_client_may_use_exactly_the_designed_flows_with_PKCE_required()
    {
        await _applications.CreateAsync(Billing());
        var client = (await _clients.FindByClientIdAsync("billing"))!;

        (await _clients.GetPermissionsAsync(client)).ShouldBe(
        [
            Permissions.Endpoints.Authorization,
            Permissions.Endpoints.Token,
            Permissions.GrantTypes.AuthorizationCode,
            Permissions.GrantTypes.ClientCredentials,
            Permissions.GrantTypes.RefreshToken,
            Permissions.ResponseTypes.Code,
        ], ignoreOrder: true);
        (await _clients.GetRequirementsAsync(client)).ShouldBe([Requirements.Features.ProofKeyForCodeExchange]);
    }

    [Fact]
    public async Task Rotating_the_secret_replaces_the_old_one()
    {
        var created = await _applications.CreateAsync(Billing());

        var newSecret = await _applications.RotateSecretAsync(created.Application.Id);

        var client = (await _clients.FindByClientIdAsync("billing"))!;
        (await _clients.ValidateClientSecretAsync(client, newSecret)).ShouldBeTrue();
        (await _clients.ValidateClientSecretAsync(client, created.ClientSecret)).ShouldBeFalse();
    }

    [Fact]
    public async Task An_application_can_be_deactivated_and_reactivated()
    {
        var created = await _applications.CreateAsync(Billing());

        await _applications.SetActiveAsync(created.Application.Id, isActive: false);
        (await _db.Applications.AsNoTracking().SingleAsync()).IsActive.ShouldBeFalse();

        await _applications.SetActiveAsync(created.Application.Id, isActive: true);
        (await _db.Applications.AsNoTracking().SingleAsync()).IsActive.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Billing")]
    [InlineData("bill.ing")]
    [InlineData("b")]
    public async Task An_invalid_key_is_rejected_and_nothing_is_created(string key)
    {
        await Should.ThrowAsync<ValidationException>(() => _applications.CreateAsync(Billing() with { Key = key }));

        await NothingWasCreated();
    }

    [Fact]
    public async Task A_duplicate_key_is_rejected_and_the_existing_client_is_untouched()
    {
        var first = await _applications.CreateAsync(Billing());

        await Should.ThrowAsync<ValidationException>(() => _applications.CreateAsync(Billing() with { Name = "Other" }));

        (await _db.Applications.CountAsync()).ShouldBe(1);
        var client = (await _clients.FindByClientIdAsync("billing"))!;
        (await _clients.ValidateClientSecretAsync(client, first.ClientSecret)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("/signin-oidc")]                     // must be absolute
    [InlineData("http://billing.example/callback")] // plain HTTP only for localhost
    public async Task A_redirect_uri_must_be_absolute_and_https_except_on_localhost(string uri)
    {
        var request = Billing() with { RedirectUris = [new Uri(uri, UriKind.RelativeOrAbsolute)] };

        await Should.ThrowAsync<ValidationException>(() => _applications.CreateAsync(request));

        await NothingWasCreated();
    }

    [Fact]
    public async Task A_localhost_http_redirect_uri_is_allowed_for_development()
    {
        var request = Billing() with { RedirectUris = [new Uri("http://localhost:5100/signin-oidc")] };

        await _applications.CreateAsync(request);

        (await _db.Applications.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task The_database_rejects_a_client_without_an_application()
    {
        // Only ApplicationService creates clients; this proves the database holds the rule even if it didn't.
        _db.Set<OpenIddictEntityFrameworkCoreApplication<Guid>>().Add(new OpenIddictEntityFrameworkCoreApplication<Guid>
        {
            ClientId = "orphan",
        });

        await DbAssert.ShouldViolate(_db, PostgresErrorCodes.ForeignKeyViolation, "fk_openiddict_applications_application");
    }

    private static NewApplication Billing() =>
        new(Key: "billing", Name: "Billing", RedirectUris: [BillingCallback], PostLogoutRedirectUris: []);

    private async Task NothingWasCreated()
    {
        _db.ChangeTracker.Clear();
        (await _db.Applications.CountAsync()).ShouldBe(0);
        (await _db.Set<OpenIddictEntityFrameworkCoreApplication<Guid>>().CountAsync()).ShouldBe(0);
    }
}
