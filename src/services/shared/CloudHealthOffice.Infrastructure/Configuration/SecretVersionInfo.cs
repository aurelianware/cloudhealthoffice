namespace CloudHealthOffice.Infrastructure.Configuration;

/// <summary>
/// Metadata describing a single version of a named secret in the underlying
/// store. Version identifiers are provider-specific — for Azure Key Vault this
/// is the opaque URL segment after the secret name.
/// </summary>
public sealed record SecretVersionInfo(
    string Version,
    DateTimeOffset? CreatedOn,
    DateTimeOffset? NotBefore,
    DateTimeOffset? ExpiresOn,
    bool Enabled);
