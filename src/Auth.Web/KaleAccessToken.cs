using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.Web;

/// <summary>The signed-in user's access token, for calling this application's APIs as that user.</summary>
public static class KaleAccessToken
{
    /// <summary>
    /// The signed-in user's current access token (already renewed for this request when it was close to expiry),
    /// or null when nobody is signed in.
    /// </summary>
    public static async Task<string?> GetKaleAccessTokenAsync(this HttpContext context)
    {
        var signedIn = await context.AuthenticateAsync(KaleSignInSetup.CookieScheme);
        return signedIn.Succeeded ? signedIn.Properties.GetTokenValue("access_token") : null;
    }

    /// <summary>
    /// Sends the signed-in user's access token with every request of this HttpClient:
    /// <c>services.AddHttpClient&lt;OrdersApi&gt;(...).AddKaleAccessToken();</c>. Use the client while handling
    /// that user's request; without a signed-in user the request goes without a token (and the API answers 401).
    /// </summary>
    public static IHttpClientBuilder AddKaleAccessToken(this IHttpClientBuilder builder) =>
        builder.AddHttpMessageHandler(services => new AccessTokenHandler(services.GetRequiredService<IHttpContextAccessor>()));

    private sealed class AccessTokenHandler(IHttpContextAccessor httpContextAccessor) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var context = httpContextAccessor.HttpContext
                ?? throw new InvalidOperationException("AddKaleAccessToken's HttpClient sends the signed-in user's token: use it while handling a request.");
            if (await context.GetKaleAccessTokenAsync() is { } accessToken)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
