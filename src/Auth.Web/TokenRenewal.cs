using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Auth.Web;

/// <summary>
/// Renews access tokens with refresh tokens at Kale's token endpoint (found through the OpenID Connect handler's
/// discovery document, over its backchannel).
/// </summary>
/// <remarks>
/// Each answer is shared, for <see cref="ShareFor"/>, by every request that brings the same refresh token. A page
/// sends several requests at once, all with the same cookie: they must not each spend the refresh token. Every
/// Kale answer has a new refresh token, and Kale accepts a replaced one for 30 seconds only, so requests still
/// carrying the old cookie within that time get this answer instead of asking again. (Several instances of the
/// application each renew once; Kale's 30 seconds cover that.) An unreachable Kale is not remembered: the next
/// request tries again.
/// </remarks>
internal sealed partial class TokenRenewal(
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions, TimeProvider clock, ILogger<TokenRenewal> logger)
{
    public static readonly TimeSpan ShareFor = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, SharedRenewal> _renewals = new(StringComparer.Ordinal);

    public Task<RenewalResult> RenewAsync(string refreshToken)
    {
        lock (_gate)
        {
            ForgetOldRenewals();
            if (!_renewals.TryGetValue(refreshToken, out var renewal))
            {
                // Task.Run: the call starts outside this lock (an async method runs synchronously up to its first await).
                renewal = new SharedRenewal(Task.Run(() => AskKaleAsync(refreshToken)), clock.GetUtcNow());
                _renewals[refreshToken] = renewal;
            }

            return renewal.Result;
        }
    }

    private void ForgetOldRenewals()
    {
        var now = clock.GetUtcNow();
        foreach (var (refreshToken, renewal) in _renewals)
        {
            var unreachable = renewal.Result.IsCompleted && renewal.Result.Result.Outcome == RenewalOutcome.Unreachable;
            if (unreachable || now - renewal.StartedAt > ShareFor)
            {
                _renewals.Remove(refreshToken);
            }
        }
    }

    // Never throws: anything that keeps Kale's answer from arriving is Unreachable, which keeps the user signed in.
    // Not tied to the request that started it, which may end before the requests sharing it do.
    private async Task<RenewalResult> AskKaleAsync(string refreshToken)
    {
        var oidc = oidcOptions.Get(KaleSignInSetup.OpenIdConnectScheme);
        try
        {
            var kale = await oidc.ConfigurationManager!.GetConfigurationAsync(CancellationToken.None);
            using var request = new HttpRequestMessage(HttpMethod.Post, kale.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                    ["client_id"] = oidc.ClientId!,
                    ["client_secret"] = oidc.ClientSecret!,
                }),
            };
            using var response = await oidc.Backchannel.SendAsync(request, CancellationToken.None);
            var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
            var result = RenewalResult.FromAnswer(response.StatusCode, body, clock.GetUtcNow());
            if (result.Outcome == RenewalOutcome.Refused)
            {
                LogRefused(logger, result.Error);
            }
            else if (result.Outcome == RenewalOutcome.Unreachable)
            {
                LogNoUsableAnswer(logger, (int)response.StatusCode);
            }

            return result;
        }
        catch (Exception exception)
        {
            LogUnreachable(logger, exception);
            return RenewalResult.Unreachable;
        }
    }

    private sealed record SharedRenewal(Task<RenewalResult> Result, DateTimeOffset StartedAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Kale refused to renew a user's token ({Error}): their Kale session has ended; signing them out.")]
    private static partial void LogRefused(ILogger logger, string? error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kale's token endpoint gave no usable answer (HTTP {Status}); the user stays signed in and the next request tries again.")]
    private static partial void LogNoUsableAnswer(ILogger logger, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't reach Kale to renew a user's token; the user stays signed in and the next request tries again.")]
    private static partial void LogUnreachable(ILogger logger, Exception exception);
}
