using System.Text.Json;

namespace AuthService.Tests.Hosting;

internal static class Jwks
{
    /// <summary>The "kid" of every key the app publishes, read through its discovery document.</summary>
    public static async Task<List<string>> KeyIds(HttpClient client)
    {
        using var discovery = JsonDocument.Parse(await client.GetStringAsync(new Uri("/.well-known/openid-configuration", UriKind.Relative)));
        var jwksUri = new Uri(discovery.RootElement.GetProperty("jwks_uri").GetString()!);
        using var jwks = JsonDocument.Parse(await client.GetStringAsync(jwksUri));
        return jwks.RootElement.GetProperty("keys").EnumerateArray()
            .Select(key => key.GetProperty("kid").GetString()!)
            .ToList();
    }
}
