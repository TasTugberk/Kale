using System.Security.Claims;
using AuthService.Grpc;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Validation;

namespace AuthService.Tests.Grpc;

/// <summary>Who may call the gRPC API: only an application, with its own client-credentials token.</summary>
[Collection(UsesPostgres.Name)]
public sealed class AppCallerTests(PostgresFixture postgres)
{
    [Fact]
    public void An_applications_own_token_is_accepted()
    {
        AppCaller.IsApplicationToken(Principal(("sub", "billing"), ("client_id", "billing"))).ShouldBeTrue();
    }

    [Fact]
    public void A_users_token_is_refused()
    {
        var user = Principal(("sub", Guid.CreateVersion7().ToString()), ("client_id", "billing"), ("sid", Guid.CreateVersion7().ToString()));

        AppCaller.IsApplicationToken(user).ShouldBeFalse();
    }

    [Fact]
    public void A_token_with_a_session_is_refused_even_if_its_subject_is_the_client()
    {
        AppCaller.IsApplicationToken(Principal(("sub", "billing"), ("client_id", "billing"), ("sid", "x"))).ShouldBeFalse();
    }

    [Fact]
    public void A_token_whose_subject_is_not_the_client_is_refused()
    {
        // e.g. a user's token without a session claim: still not the application acting as itself.
        AppCaller.IsApplicationToken(Principal(("sub", Guid.CreateVersion7().ToString()), ("client_id", "billing"))).ShouldBeFalse();
    }

    [Fact]
    public void A_token_without_a_client_is_refused()
    {
        AppCaller.IsApplicationToken(Principal(("sub", "billing"))).ShouldBeFalse();
    }

    [Fact]
    public async Task Only_tokens_for_the_API_audience_are_accepted()
    {
        // Without a configured audience, OpenIddict's validation would accept tokens for any audience,
        // e.g. a user's token for an application.
        await using var app = await AuthServiceApp.CreateAsync(postgres);

        var validation = app.Services.GetRequiredService<IOptionsMonitor<OpenIddictValidationOptions>>().CurrentValue;

        validation.Audiences.ShouldBe(["urn:kale:auth-api"]);
    }

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), authenticationType: "test"));
}
