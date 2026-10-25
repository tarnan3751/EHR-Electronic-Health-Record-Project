using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Ehr.Web.Tests;

// The codes an authenticator app would show, from the key Identity stores (RFC 6238: HMAC-SHA1, 30-second steps, six
// digits, the key in RFC 4648 base32). The tests stand in for the phone.
static class Totp
{
    const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Code(string base32Key) => Code(base32Key, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);

    public static string Code(string base32Key, long step)
    {
        var counter = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(counter, (ulong)step);
        var hash = HMACSHA1.HashData(FromBase32(base32Key), counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    // A code the app would not show now: the right one for a step well outside the verifier's window.
    public static string WrongCode(string base32Key) => Code(base32Key, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + 1000);

    static byte[] FromBase32(string input)
    {
        var output = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var character in input.TrimEnd('=').ToUpperInvariant())
        {
            buffer = (buffer << 5) | Base32Alphabet.IndexOf(character);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. output];
    }
}
