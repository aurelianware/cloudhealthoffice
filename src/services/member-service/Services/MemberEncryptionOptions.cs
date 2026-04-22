namespace MemberService.Services;

/// <summary>
/// Config surface for <see cref="KeyVaultIdentifierEncryptor"/>. Defaults
/// match a first-deploy service — no explicit MemberEncryption section
/// and v1 as the implied current version. The <see cref="LegacyKeySecretName"/>
/// is the secret name used to decrypt envelopes emitted by the pre-A.7.3
/// encryptor (format-version 0x01).
/// </summary>
public sealed record MemberEncryptionOptions
{
    public const string SectionName = "MemberEncryption";

    public string KeySecretPrefix { get; init; } = "member-identifier-encryption-key";
    public string CurrentKeyVersion { get; init; } = "v1";
    public IReadOnlyList<string> AcceptedKeyVersions { get; init; } = new[] { "v1" };
    public string? LegacyKeySecretName { get; init; } = "member-identifier-encryption-key";

    /// <summary>
    /// When true, <see cref="KeyVaultIdentifierEncryptor.EncryptAsync"/> emits
    /// 0x01 (legacy) envelopes resolved via <see cref="LegacyKeySecretName"/>
    /// instead of 0x02 envelopes resolved via the rotating prefix. Set only
    /// by the legacy-config bridge in Program.cs so a service booting with
    /// only the pre-A.7.3 <c>Member:IdentifierEncryption:KeySecretName</c>
    /// key continues to write the exact same envelope shape and can't produce
    /// 0x02 envelopes referencing a prefix-versioned secret that doesn't exist.
    /// When the operator adds an explicit <c>MemberEncryption</c> section,
    /// this flag stays false and new writes become 0x02.
    /// </summary>
    public bool EmitLegacyEnvelope { get; init; } = false;
}
