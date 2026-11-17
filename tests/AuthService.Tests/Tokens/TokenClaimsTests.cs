using System.Collections.Immutable;
using AuthService.Tokens;

namespace AuthService.Tests.Tokens;

/// <summary>
/// What a code carries to the code exchange (the role, the security stamp) must not reach the session's tokens.
/// OpenIddict copies every claim of the principal into the refresh token, whatever its destinations, so
/// <see cref="TokenClaims.StartSession"/> must remove them; SignInFlowTests checks the readable tokens as well.
/// </summary>
public sealed class TokenClaimsTests
{
    [Fact]
    public void Starting_the_session_removes_the_codes_role_and_security_stamp()
    {
        var principal = TokenClaims.ForCode(Guid.CreateVersion7(), Guid.CreateVersion7(), "stamp", "billing", ImmutableArray.Create("openid"));

        TokenClaims.StartSession(principal, Guid.CreateVersion7());

        principal.HasClaim(c => c.Type == TokenClaims.RoleId).ShouldBeFalse();
        principal.HasClaim(c => c.Type == TokenClaims.SecurityStamp).ShouldBeFalse();
        principal.HasClaim(c => c.Type == TokenClaims.SessionId).ShouldBeTrue();
    }
}
