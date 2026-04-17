namespace MemberService.Services;

/// <summary>
/// Deterministic fingerprint for PII identifier values. Used ONLY for
/// duplicate detection — never for lookup beyond equality comparison, never
/// reversed. Implementations must use a KV-backed HMAC key that is DISTINCT
/// from the AES-GCM encryption key so the two secrets can rotate independently.
/// </summary>
public interface IIdentifierFingerprinter
{
    /// <summary>True when the implementation performs real HMAC (false = dev no-op).</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Returns a stable fingerprint for the given already-normalized plaintext.
    /// Same input → same output (for the lifetime of the HMAC key).
    /// </summary>
    Task<string> FingerprintAsync(string normalizedPlaintext, CancellationToken ct = default);
}
