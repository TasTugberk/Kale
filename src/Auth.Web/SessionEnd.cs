using System.Text.Json;
using Auth.Contracts.V1;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Auth.Web;

/// <summary>
/// Ends the user's Kale session from this server when they sign out (Kale's gRPC EndSession, with this
/// application's own client-credentials token). The browser is still sent to Kale's sign-out page, which ends
/// Kale's own sign-in cookie; but it may never get there (a closed tab), or get there after that cookie ran out,
/// and the session must end anyway.
/// </summary>
internal sealed partial class SessionEnd(
    Uri grpcAddress, IOptionsMonitor<OpenIdConnectOptions> oidcOptions, ILogger<SessionEnd> logger)
{
    // A user waits for this before leaving; Kale.Auth.Client's lookups give Kale the same time.
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Ends the session named by the ID token's "sid". Never throws: a failure is logged, and the sign-out goes on
    /// (the session then ends at Kale's sign-out page, or when it expires).
    /// </summary>
    public async Task EndAsync(string idToken)
    {
        var sessionId = SessionIdOf(idToken);
        if (sessionId is null)
        {
            return;
        }

        try
        {
            var oidc = oidcOptions.Get(KaleSignInSetup.OpenIdConnectScheme);
            var appToken = await AppTokenAsync(oidc);

            // The OpenID Connect handler's backchannel reaches Kale already (with whatever an application set up for
            // it, e.g. a proxy). A sign-out is rare, so a channel per call is cheap enough and nothing to keep.
            using var channel = GrpcChannel.ForAddress(grpcAddress, new GrpcChannelOptions { HttpClient = oidc.Backchannel, DisposeHttpClient = false });
            await new SessionService.SessionServiceClient(channel).EndSessionAsync(
                new EndSessionRequest { SessionId = sessionId },
                new Metadata { { "Authorization", $"Bearer {appToken}" } },
                deadline: DateTime.UtcNow + Deadline);
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.NotFound)
        {
            // Kale no longer has it (finished sessions are deleted after a while): nothing to end.
        }
        catch (Exception exception)
        {
            LogFailed(logger, exception);
        }
    }

    private static string? SessionIdOf(string idToken)
    {
        try
        {
            return new JsonWebToken(idToken).TryGetPayloadValue<string>("sid", out var sessionId) ? sessionId : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // This application's own token for Kale's gRPC API (client credentials). Not kept: one per sign-out.
    private static async Task<string> AppTokenAsync(OpenIdConnectOptions oidc)
    {
        var kale = await oidc.ConfigurationManager!.GetConfigurationAsync(CancellationToken.None);
        using var request = new HttpRequestMessage(HttpMethod.Post, kale.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = oidc.ClientId!,
                ["client_secret"] = oidc.ClientSecret!,
            }),
        };
        using var response = await oidc.Backchannel.SendAsync(request, CancellationToken.None);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        return body.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Kale's token answer has no access_token.");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't end the user's Kale session from the server; the sign-out goes on (Kale's sign-out page ends it, or it expires).")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
