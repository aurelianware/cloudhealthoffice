using System.ComponentModel;

namespace CloudHealthOffice.Infrastructure.Configuration;

/// <summary>
/// Identifies the backing secret store implementation.
/// </summary>
public enum SecretProviderType
{
    /// <summary>No secret provider configured. The <see cref="NullSecretProvider"/> is used.</summary>
    [Description("No secret provider — secrets are not managed externally")]
    None = 0,

    /// <summary>Azure Key Vault with Workload Identity authentication.</summary>
    [Description("Azure Key Vault with Workload Identity")]
    AzureKeyVault = 1,

    /// <summary>HashiCorp Vault with Kubernetes auth.</summary>
    [Description("HashiCorp Vault with Kubernetes auth")]
    HashiCorpVault = 2
}
