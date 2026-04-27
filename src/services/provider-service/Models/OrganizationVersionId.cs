using System.Security.Cryptography;

namespace ProviderService.Models;

/// <summary>
/// ULID generator for <see cref="Organization"/> version rows. Mirrors
/// <c>ProviderVersionId</c> — Crockford base-32, 26 chars, 48-bit timestamp
/// + 80-bit randomness. Lexicographic ordering tracks creation time so
/// the latest activated version sorts last under a stable string compare.
/// </summary>
internal static class OrganizationVersionId
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string NewId() => NewId(DateTimeOffset.UtcNow);

    internal static string NewId(DateTimeOffset timestamp)
    {
        var ms = timestamp.ToUnixTimeMilliseconds();
        if (ms < 0) ms = 0;

        Span<byte> rand = stackalloc byte[10];
        RandomNumberGenerator.Fill(rand);

        Span<char> chars = stackalloc char[26];

        for (int i = 9; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(ms & 0x1F)];
            ms >>= 5;
        }

        ulong hi = ((ulong)rand[0] << 32) | ((ulong)rand[1] << 24) | ((ulong)rand[2] << 16)
                 | ((ulong)rand[3] << 8) | rand[4];
        ulong lo = ((ulong)rand[5] << 32) | ((ulong)rand[6] << 24) | ((ulong)rand[7] << 16)
                 | ((ulong)rand[8] << 8) | rand[9];

        for (int i = 17; i >= 10; i--)
        {
            chars[i] = Alphabet[(int)(hi & 0x1F)];
            hi >>= 5;
        }
        for (int i = 25; i >= 18; i--)
        {
            chars[i] = Alphabet[(int)(lo & 0x1F)];
            lo >>= 5;
        }

        return new string(chars);
    }
}
