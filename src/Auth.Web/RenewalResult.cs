using System.Net;
using System.Text.Json;

namespace Auth.Web;

internal enum RenewalOutcome
{
    /// <summary>Kale gave new tokens.</summary>
    Renewed,

    /// <summary>Kale said no (an OAuth error such as invalid_grant): the Kale session has ended. Sign the user out.</summary>
    Refused,

    /// <summary>No usable answer (network, timeout, a proxy's error page, Kale failing): keep the user signed in.</summary>
    Unreachable,
}

/// <summary>The outcome of renewing an access token with a refresh token at Kale's token endpoint.</summary>
internal sealed record RenewalResult(RenewalOutcome Outcome)
{
    public static readonly RenewalResult Unreachable = new(RenewalOutcome.Unreachable);

    public string? AccessToken { get; init; }

    /// <summary>The new refresh token; null when the answer had none (then the old one stays).</summary>
    public string? RefreshToken { get; init; }

    /// <summary>The new ID token; null when the answer had none (then the old one stays).</summary>
    public string? IdToken { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The OAuth error of a refusal, e.g. invalid_grant.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Reads Kale's answer. Only a 4xx with an OAuth "error" is a refusal: a 5xx (even OAuth's server_error), or a
    /// 4xx without one (a proxy's page, rate limiting), says nothing about the session, and must not sign anyone out.
    /// </summary>
    public static RenewalResult FromAnswer(HttpStatusCode status, string body, DateTimeOffset receivedAt)
    {
        JsonElement answer;
        try
        {
            using var document = JsonDocument.Parse(body);
            answer = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Unreachable;
        }

        if (answer.ValueKind != JsonValueKind.Object)
        {
            return Unreachable;
        }

        var code = (int)status;
        if (code is >= 200 and < 300 && Text(answer, "access_token") is { } accessToken
            && answer.TryGetProperty("expires_in", out var expiresIn) && expiresIn.TryGetInt32(out var seconds))
        {
            return new RenewalResult(RenewalOutcome.Renewed)
            {
                AccessToken = accessToken,
                RefreshToken = Text(answer, "refresh_token"),
                IdToken = Text(answer, "id_token"),
                ExpiresAt = receivedAt.AddSeconds(seconds),
            };
        }

        if (code is >= 400 and < 500 && Text(answer, "error") is { } error)
        {
            return new RenewalResult(RenewalOutcome.Refused) { Error = error };
        }

        return Unreachable;
    }

    private static string? Text(JsonElement answer, string name) =>
        answer.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
