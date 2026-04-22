namespace ConsentService.Services;

/// <summary>
/// Config surface for <see cref="ConsentFieldEncryptor"/>. consent-service is a
/// greenfield service — there are no 0x01 legacy envelopes to decrypt, so
/// the options shape does NOT carry a <c>LegacyKeySecretName</c> (unlike
/// <c>MemberEncryptionOptions</c>). If a reviewer asks why consent differs
/// from member: member has legacy 0x01 envelopes from before A.7.3; consent
/// doesn't.
/// </summary>
public sealed record ConsentEncryptionOptions
{
    public const string SectionName = "ConsentEncryption";

    public string KeySecretPrefix { get; init; } = "consent-body-encryption-key";
    public string CurrentKeyVersion { get; init; } = "v1";
    public IReadOnlyList<string> AcceptedKeyVersions { get; init; } = new[] { "v1" };
}
