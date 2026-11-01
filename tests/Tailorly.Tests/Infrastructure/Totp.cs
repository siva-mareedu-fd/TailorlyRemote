using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Tailorly.Tests.Infrastructure;

/// <summary>RFC 6238 codes (SHA-1, 30 s, 6 digits), as an authenticator app computes them from the Base32 shared key.</summary>
public static class Totp
{
    public static string Code(string sharedKey, DateTimeOffset at)
    {
        var key = Base32(sharedKey.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant());
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, at.ToUnixTimeSeconds() / 30);
        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var value = (BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset)) & 0x7FFFFFFF) % 1_000_000;
        return value.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A valid code that is not the current one: the window accepts ±2 steps, so 10 minutes away is always wrong.</summary>
    public static string WrongCode(string sharedKey, DateTimeOffset at) => Code(sharedKey, at.AddMinutes(10));

    private static byte[] Base32(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text.TrimEnd('='))
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }
        return [.. bytes];
    }
}
