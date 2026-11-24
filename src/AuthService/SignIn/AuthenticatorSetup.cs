using QRCoder;

namespace AuthService.SignIn;

/// <summary>
/// What the setup page shows to add Kale to an authenticator app: the key as text and the otpauth:// address it
/// stands for (the Key Uri Format that authenticator apps read), drawn as a QR code on the server.
/// </summary>
public static class AuthenticatorSetup
{
    /// <summary>The name the app shows next to the codes, before the user name.</summary>
    public const string Issuer = "Kale";

    public static string Uri(string userName, string key) =>
        $"otpauth://totp/{System.Uri.EscapeDataString(Issuer)}:{System.Uri.EscapeDataString(userName)}" +
        $"?secret={key}&issuer={System.Uri.EscapeDataString(Issuer)}&digits=6";

    /// <summary>The key in groups of four, easier to type by hand.</summary>
    public static string Readable(string key) =>
        string.Join(' ', key.Chunk(4).Select(group => new string(group)));

    /// <summary>
    /// The QR code as an inline SVG element: no image file and no script (the pages work on isolated networks).
    /// Black on white in both themes, since not every app reads light-on-dark codes; it scales with its box.
    /// </summary>
    public static string QrCodeSvg(string uri)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(4, "#000000", "#ffffff", drawQuietZones: true, SvgQRCode.SizingMode.ViewBoxAttribute);
    }
}
