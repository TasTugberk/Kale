using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AuthService.Tests.SignIn;

/// <summary>
/// What an authenticator app shows for a key (RFC 6238: HMAC-SHA1, 30-second steps, 6 digits), computed here
/// rather than with Identity's own helper, which is internal. Identity checks codes against the real clock
/// (DateTime.UtcNow, not the app's TimeProvider), so this uses it too.
/// </summary>
internal static class Totp
{
    public static string Code(string base32Key) => Code(base32Key, DateTimeOffset.UtcNow);

    public static string Code(string base32Key, DateTimeOffset at)
    {
        var step = (ulong)(at.ToUnixTimeSeconds() / 30);
        var counter = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(counter, step);
#pragma warning disable CA5350 // RFC 6238 and every authenticator app use HMAC-SHA1; that is what Identity checks.
        var hash = HMACSHA1.HashData(FromBase32(base32Key), counter);
#pragma warning restore CA5350

        // RFC 4226's dynamic truncation: 4 bytes from the offset in the last nibble, without the sign bit.
        var offset = hash[^1] & 0x0F;
        var number = BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset)) & 0x7FFFFFFF;
        return (number % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] FromBase32(string key)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var buffer = 0;
        var bytes = new List<byte>();
        foreach (var character in key.Replace(" ", "", StringComparison.Ordinal).TrimEnd('=').ToUpperInvariant())
        {
            buffer = (buffer << 5) | alphabet.IndexOf(character, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }

        return [.. bytes];
    }
}
