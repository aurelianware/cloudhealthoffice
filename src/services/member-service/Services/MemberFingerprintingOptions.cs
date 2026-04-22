namespace MemberService.Services;

/// <summary>
/// Config surface for <see cref="HmacSha256IdentifierFingerprinter"/>.
/// Separate from <see cref="MemberEncryptionOptions"/> because the
/// fingerprint key MUST be distinct from the AES-GCM encryption key so
/// the two secrets can rotate independently.
///
/// Defaults match a first-deploy service — no explicit
/// MemberFingerprinting block and v1 as the implied current version.
/// </summary>
public sealed record MemberFingerprintingOptions
{
    public const string SectionName = "MemberFingerprinting";

    public string KeySecretPrefix { get; init; } = "member-identifier-fingerprint-key";
    public string CurrentKeyVersion { get; init; } = "v1";
    public IReadOnlyList<string> AcceptedKeyVersions { get; init; } = new[] { "v1" };

    /// <summary>
    /// Secret name used by the pre-A.7.3 single-key fingerprinter.
    /// Resolved as an implicit "v1" entry when the operator hasn't
    /// published <c>{KeySecretPrefix}-v1</c> yet — preserves dedupe
    /// for rows written before rotation support landed.
    /// </summary>
    public string? LegacyKeySecretName { get; init; } = "member-identifier-fingerprint-key";
}
