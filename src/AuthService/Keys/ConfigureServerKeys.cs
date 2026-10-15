using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;

namespace AuthService.Keys;

/// <summary>Gives OpenIddict the keys from <see cref="KeyRing"/> each time its options are (re)built.</summary>
public sealed class ConfigureServerKeys(ServerKeyRefresher refresher) : IConfigureOptions<OpenIddictServerOptions>
{
    public void Configure(OpenIddictServerOptions options)
    {
        var keys = refresher.GetOrLoad();

        // OpenIddict signs and encrypts with the first credential of each list; the others are only accepted
        // (and signing keys published in JWKS). KeyRing puts the current key first.
        foreach (var key in keys.Signing.All)
        {
            options.SigningCredentials.Add(new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        }

        foreach (var key in keys.Encryption.All)
        {
            options.EncryptionCredentials.Add(new EncryptingCredentials(
                key, SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512));
        }
    }
}
