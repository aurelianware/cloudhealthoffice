namespace PersonalRepresentativeService.Services;

/// <summary>
/// Config surface for <see cref="PersonalRepFieldEncryptor"/>.
/// personal-representative-service is a greenfield service — there are no
/// 0x01 legacy envelopes to decrypt, so the options shape does NOT carry a
/// <c>LegacyKeySecretName</c>. If a reviewer asks why this differs from
/// member: member has legacy 0x01 envelopes from before A.7.3;
/// personal-rep doesn't.
/// </summary>
public sealed record PersonalRepEncryptionOptions
{
    public const string SectionName = "PersonalRepEncryption";

    public string KeySecretPrefix { get; init; } = "personal-rep-body-encryption-key";
    public string CurrentKeyVersion { get; init; } = "v1";
    public IReadOnlyList<string> AcceptedKeyVersions { get; init; } = new[] { "v1" };
}
