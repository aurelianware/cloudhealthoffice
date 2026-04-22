using System.Security.Cryptography;
using System.Text;

namespace MemberService.Services;

/// <summary>
/// Dev-only fingerprinter. Returns a plain SHA-256 hash (no keyed HMAC).
/// Dedupe still works; cryptographic guarantees do not. Never register in prod.
/// </summary>
public sealed class NoOpIdentifierFingerprinter : IIdentifierFingerprinter
{
    public bool IsEnabled => false;

    public Task<string> FingerprintAsync(string normalizedPlaintext, CancellationToken ct = default)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPlaintext ?? string.Empty));
        return Task.FromResult(Convert.ToHexString(bytes));
    }

    public async Task<IReadOnlyList<string>> FingerprintCandidatesAsync(
        string normalizedPlaintext, CancellationToken ct = default)
    {
        // Dev shim: a single candidate equal to the deterministic SHA-256.
        return new[] { await FingerprintAsync(normalizedPlaintext, ct) };
    }
}
