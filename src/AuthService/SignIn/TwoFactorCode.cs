namespace AuthService.SignIn;

/// <summary>
/// What a user typed into a code field: the 6 digits their authenticator app shows, or one of their recovery codes
/// (Identity's format, XXXXX-XXXXX). Typed or pasted as people do: with spaces, without the dash, in lowercase.
/// </summary>
public sealed record TwoFactorCode(string Value, bool IsFromAuthenticatorApp)
{
    public static TwoFactorCode From(string? typed)
    {
        var plain = new string((typed ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();
        if (plain.Length == 6 && plain.All(char.IsAsciiDigit))
        {
            return new TwoFactorCode(plain, IsFromAuthenticatorApp: true);
        }

        return new TwoFactorCode(plain.Length == 10 ? $"{plain[..5]}-{plain[5..]}" : plain, IsFromAuthenticatorApp: false);
    }
}
