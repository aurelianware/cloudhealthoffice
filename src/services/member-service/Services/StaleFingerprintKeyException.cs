namespace MemberService.Services;

/// <summary>
/// Thrown by <see cref="HmacSha256IdentifierFingerprinter.FingerprintCandidatesAsync"/>
/// when an accepted key version cannot be resolved from the secret
/// provider. Distinct from <see cref="StaleEncryptionKeyException"/> so
/// callers can 503 specifically when the dedupe system is degraded —
/// silently producing a partial candidate set could let duplicate PII
/// identifiers past the uniqueness check, which is a correctness
/// violation, not a degraded-but-serve one.
///
/// The health check already surfaces missing versions as Degraded; the
/// throw here is the read-side enforcement so a misconfigured window
/// doesn't reach the dedupe path.
/// </summary>
public sealed class StaleFingerprintKeyException : InvalidOperationException
{
    public string KeyVersion { get; }

    public StaleFingerprintKeyException(string keyVersion, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        KeyVersion = keyVersion;
    }
}
