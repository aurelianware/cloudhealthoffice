namespace AppealsService.Services;

/// <summary>
/// Config surface for <see cref="AppealFieldEncryptor"/>. appeals-service is
/// greenfield with respect to field-level encryption — appeals has NEVER
/// stored envelope-encrypted records before this PR, so the options shape
/// does NOT carry a <c>LegacyKeySecretName</c>. Only 0x02 envelopes are
/// read and written.
/// </summary>
public sealed record AppealEncryptionOptions
{
    public const string SectionName = "AppealEncryption";

    public string KeySecretPrefix { get; init; } = "appeal-body-encryption-key";
    public string CurrentKeyVersion { get; init; } = "v1";
    public IReadOnlyList<string> AcceptedKeyVersions { get; init; } = new[] { "v1" };
}
