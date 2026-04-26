using System.Security.Cryptography;

namespace BenefitPlanService.Models;

/// <summary>
/// Generates ULID-shaped identifiers (Crockford base-32, 26 chars,
/// 48-bit timestamp + 80-bit randomness) for plan versions.
///
/// We inline a minimal generator rather than take a dependency: the only
/// requirements are lexicographic monotonicity by creation time and global
/// uniqueness — which a millisecond-prefixed ULID satisfies.
/// </summary>
internal static class PlanVersionId
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

        // 48-bit timestamp → 10 base-32 chars
        for (int i = 9; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(ms & 0x1F)];
            ms >>= 5;
        }

        // 80-bit randomness → 16 base-32 chars
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
