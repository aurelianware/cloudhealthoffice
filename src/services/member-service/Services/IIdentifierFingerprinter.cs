namespace MemberService.Services;

/// <summary>
/// Deterministic fingerprint for PII identifier values. Used ONLY for
/// duplicate detection — never for lookup beyond equality comparison,
/// never reversed. Implementations must use a KV-backed HMAC key that is
/// DISTINCT from the AES-GCM encryption key so the two secrets can rotate
/// independently.
/// </summary>
public interface IIdentifierFingerprinter
{
    /// <summary>True when the implementation performs real HMAC (false = dev no-op).</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Returns a stable fingerprint for the given already-normalized plaintext
    /// under the CURRENT key version. Same input → same output (until the
    /// current key version rotates). This is the WRITE path — what gets
    /// persisted alongside the encrypted identifier.
    /// </summary>
    Task<string> FingerprintAsync(string normalizedPlaintext, CancellationToken ct = default);

    /// <summary>
    /// Returns a fingerprint under every accepted key version — newest first.
    /// Used by READ paths (dedupe checks, lookups, removals) so a record
    /// fingerprinted under the previous key version still matches when the
    /// current version has rotated. Callers should do
    /// <c>candidates.Contains(storedFingerprint)</c> or SQL <c>IN (...)</c>.
    /// </summary>
    Task<IReadOnlyList<string>> FingerprintCandidatesAsync(
        string normalizedPlaintext,
        CancellationToken ct = default);
}
