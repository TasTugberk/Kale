namespace AuthService.Domain;

public enum KeyUse
{
    /// <summary>Signs tokens (access and identity tokens); the public half is published in JWKS.</summary>
    Signing,

    /// <summary>Encrypts what only AuthService reads back: authorization codes and refresh tokens.</summary>
    Encryption,
}

/// <summary>
/// One RSA key of the auth server. Generated and rotated by <c>KeyRing</c>, never entered by hand.
/// </summary>
public sealed class ServerKey : BaseEntity
{
    public KeyUse Use { get; init; }

    /// <summary>The private key (PEM), encrypted with ASP.NET Core Data Protection. Never stored in plain text.</summary>
    public required string ProtectedPrivateKey { get; init; }
}
