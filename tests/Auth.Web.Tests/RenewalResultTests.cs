using System.Net;
using Auth.Web;

namespace Auth.Web.Tests;

/// <summary>Kale's answer to a refresh: new tokens, a refusal (sign the user out), or no usable answer (keep them).</summary>
public sealed class RenewalResultTests
{
    private static readonly DateTimeOffset ReceivedAt = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void New_tokens_are_read_with_their_expiry_counted_from_when_the_answer_came()
    {
        var result = RenewalResult.FromAnswer(HttpStatusCode.OK, """
            {"access_token":"new-access","refresh_token":"new-refresh","id_token":"new-id","expires_in":600,"token_type":"Bearer"}
            """, ReceivedAt);

        result.Outcome.ShouldBe(RenewalOutcome.Renewed);
        result.AccessToken.ShouldBe("new-access");
        result.RefreshToken.ShouldBe("new-refresh");
        result.IdToken.ShouldBe("new-id");
        result.ExpiresAt.ShouldBe(ReceivedAt.AddSeconds(600));
    }

    [Fact]
    public void An_answer_without_a_new_refresh_or_id_token_keeps_the_old_ones()
    {
        var result = RenewalResult.FromAnswer(HttpStatusCode.OK, """{"access_token":"new-access","expires_in":600}""", ReceivedAt);

        result.Outcome.ShouldBe(RenewalOutcome.Renewed);
        result.RefreshToken.ShouldBeNull();
        result.IdToken.ShouldBeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "invalid_grant")]
    [InlineData(HttpStatusCode.Unauthorized, "invalid_client")]
    public void An_OAuth_error_answer_is_a_refusal(HttpStatusCode status, string error)
    {
        var result = RenewalResult.FromAnswer(status, $$"""{"error":"{{error}}","error_description":"The session is no longer active."}""", ReceivedAt);

        result.Outcome.ShouldBe(RenewalOutcome.Refused);
        result.Error.ShouldBe(error);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "")]
    [InlineData(HttpStatusCode.BadGateway, "<html>Bad gateway</html>")]
    [InlineData(HttpStatusCode.InternalServerError, """{"error":"server_error"}""")]
    [InlineData(HttpStatusCode.NotFound, "<html>Not found</html>")]
    [InlineData(HttpStatusCode.TooManyRequests, "")]
    [InlineData(HttpStatusCode.OK, """{"token_type":"Bearer"}""")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public void Anything_else_is_no_usable_answer(HttpStatusCode status, string body)
    {
        RenewalResult.FromAnswer(status, body, ReceivedAt).Outcome.ShouldBe(RenewalOutcome.Unreachable);
    }
}
