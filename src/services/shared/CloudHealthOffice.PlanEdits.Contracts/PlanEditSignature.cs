using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CloudHealthOffice.PlanEdits.Contracts;

/// <summary>
/// The request signature: HMAC-SHA256, keyed with the secret the plan and
/// CHO share, over <c>{timestamp}.{body}</c> — the
/// <see cref="PlanEditContract.TimestampHeader"/> value, a full stop, and the
/// exact request body bytes. Sent as <c>v1={lower-case hex}</c> in
/// <see cref="PlanEditContract.SignatureHeader"/>. A plan service verifies it
/// before it trusts the request, and refuses a timestamp more than
/// <see cref="PlanEditContract.SignatureTolerance"/> from its own clock.
/// </summary>
public static class PlanEditSignature
{
    public const string Scheme = "v1=";

    /// <summary>The <see cref="PlanEditContract.SignatureHeader"/> value for a request.</summary>
    public static string Sign(string secret, long timestamp, ReadOnlySpan<byte> body)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        return Scheme + Convert.ToHexString(Compute(secret, timestamp, body)).ToLowerInvariant();
    }

    /// <summary>
    /// Whether <paramref name="signatureHeader"/> is the signature of
    /// <paramref name="body"/> at <paramref name="timestampHeader"/> and the
    /// timestamp is within <see cref="PlanEditContract.SignatureTolerance"/>
    /// of <paramref name="now"/>. Compares in constant time.
    /// </summary>
    public static bool Verify(
        string secret, string? timestampHeader, string? signatureHeader, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(timestampHeader) || string.IsNullOrEmpty(signatureHeader))
            return false;
        if (!long.TryParse(timestampHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp))
            return false;

        var age = now - DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(timestamp, 0, 253402300799));
        if (age.Duration() > PlanEditContract.SignatureTolerance)
            return false;

        if (!signatureHeader.StartsWith(Scheme, StringComparison.Ordinal))
            return false;
        byte[] presented;
        try
        {
            presented = Convert.FromHexString(signatureHeader.AsSpan(Scheme.Length));
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(presented, Compute(secret, timestamp, body));
    }

    private static byte[] Compute(string secret, long timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.ASCII.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + ".");
        var message = new byte[prefix.Length + body.Length];
        prefix.CopyTo(message, 0);
        body.CopyTo(message.AsSpan(prefix.Length));
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message);
    }
}
