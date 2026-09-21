namespace CloudHealthOffice.Infrastructure.Configuration;

/// <summary>
/// Configuration options for the secret provider, bound from the <c>SecretProvider</c> config section.
/// </summary>
public record SecretProviderOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "SecretProvider";

    /// <summary>Which secret store implementation to use.</summary>
    public SecretProviderType Provider { get; init; } = SecretProviderType.None;

    /// <summary>Azure Key Vault URI (e.g. <c>https://my-vault.vault.azure.net/</c>). Required when <see cref="Provider"/> is <see cref="SecretProviderType.AzureKeyVault"/>.</summary>
    public string? AzureKeyVaultUri { get; init; }

    /// <summary>HashiCorp Vault address (e.g. <c>https://vault.internal:8200</c>). Required when <see cref="Provider"/> is <see cref="SecretProviderType.HashiCorpVault"/>.</summary>
    public string? HashiCorpVaultAddress { get; init; }

    /// <summary>HashiCorp Vault KV mount point. Defaults to <c>secret</c> if not specified.</summary>
    public string? HashiCorpVaultMountPoint { get; init; }

    /// <summary>
    /// Vault Kubernetes auth role. When set, the provider logs in with the pod's projected
    /// service account token, which is how a workload authenticates without a long-lived secret.
    /// </summary>
    public string? HashiCorpVaultKubernetesRole { get; init; }

    /// <summary>Vault Kubernetes auth mount path. Defaults to <c>kubernetes</c>.</summary>
    public string? HashiCorpVaultKubernetesAuthPath { get; init; }

    /// <summary>
    /// Path to the projected service account token used for Kubernetes auth. Defaults to the
    /// standard mount, which every pod receives.
    /// </summary>
    public string? HashiCorpVaultServiceAccountTokenPath { get; init; }

    /// <summary>
    /// A Vault token used directly instead of Kubernetes auth. Intended for local development
    /// and tests; a production workload should authenticate with
    /// <see cref="HashiCorpVaultKubernetesRole"/> rather than carrying a static token.
    /// </summary>
    public string? HashiCorpVaultToken { get; init; }

    /// <summary>Interval in seconds at which secrets are reloaded from the provider. Defaults to 300 (5 minutes).</summary>
    public int ReloadIntervalSeconds { get; init; } = 300;

    /// <summary>
    /// When <c>true</c>, a reload failure logs a warning and preserves existing secret values.
    /// When <c>false</c>, a reload failure throws and may crash the application.
    /// </summary>
    public bool GracefulDegradation { get; init; } = true;
}
