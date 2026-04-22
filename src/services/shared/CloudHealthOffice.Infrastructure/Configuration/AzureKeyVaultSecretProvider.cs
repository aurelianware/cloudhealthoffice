using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Configuration;

/// <summary>
/// <see cref="ISecretProvider"/> implementation backed by Azure Key Vault.
/// Uses <see cref="DefaultAzureCredential"/> so authentication works transparently with
/// AKS Workload Identity, Managed Identity, and local developer credentials (az login / VS).
/// </summary>
public sealed class AzureKeyVaultSecretProvider : ISecretProvider
{
    private readonly SecretClient _client;
    private readonly ILogger<AzureKeyVaultSecretProvider> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="AzureKeyVaultSecretProvider"/>.
    /// </summary>
    /// <param name="options">Secret provider options containing the Key Vault URI.</param>
    /// <param name="logger">Logger instance.</param>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="SecretProviderOptions.AzureKeyVaultUri"/> is not configured.</exception>
    public AzureKeyVaultSecretProvider(SecretProviderOptions options, ILogger<AzureKeyVaultSecretProvider> logger)
    {
        if (string.IsNullOrWhiteSpace(options.AzureKeyVaultUri))
        {
            throw new InvalidOperationException(
                $"SecretProvider:AzureKeyVaultUri must be configured when Provider is {SecretProviderType.AzureKeyVault}.");
        }

        _client = new SecretClient(new Uri(options.AzureKeyVaultUri), new DefaultAzureCredential());
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> GetSecretAsync(string secretName, CancellationToken ct = default)
    {
        _logger.LogDebug("Retrieving secret '{SecretName}' from Azure Key Vault", secretName);

        try
        {
            KeyVaultSecret secret = await _client.GetSecretAsync(secretName, cancellationToken: ct);
            return secret.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogDebug("Secret '{SecretName}' not found in Azure Key Vault", secretName);
            return null;
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Failed to retrieve secret '{SecretName}' from Azure Key Vault (HTTP {Status})",
                secretName, ex.Status);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, string>> GetSecretsAsync(string prefix, CancellationToken ct = default)
    {
        _logger.LogDebug("Listing secrets with prefix '{Prefix}' from Azure Key Vault", prefix);

        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await foreach (SecretProperties properties in _client.GetPropertiesOfSecretsAsync(ct))
        {
            if (!properties.Enabled.GetValueOrDefault())
                continue;

            if (!string.IsNullOrEmpty(prefix) &&
                !properties.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            _logger.LogDebug("Fetching secret '{SecretName}'", properties.Name);

            KeyVaultSecret secret = await _client.GetSecretAsync(properties.Name, cancellationToken: ct);
            results[properties.Name] = secret.Value;
        }

        _logger.LogDebug("Retrieved {Count} secret(s) matching prefix '{Prefix}'", results.Count, prefix);
        return results;
    }

    /// <inheritdoc />
    public async Task<string?> GetSecretByVersionAsync(
        string secretName, string version, CancellationToken ct = default)
    {
        _logger.LogDebug("Retrieving secret '{SecretName}' version '{Version}' from Azure Key Vault",
            secretName, version);

        try
        {
            KeyVaultSecret secret = await _client.GetSecretAsync(secretName, version, cancellationToken: ct);
            return secret.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogDebug("Secret '{SecretName}' version '{Version}' not found in Azure Key Vault",
                secretName, version);
            return null;
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex,
                "Failed to retrieve secret '{SecretName}' version '{Version}' from Azure Key Vault (HTTP {Status})",
                secretName, version, ex.Status);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SecretVersionInfo>> ListSecretVersionsAsync(
        string secretName, CancellationToken ct = default)
    {
        _logger.LogDebug("Listing versions of secret '{SecretName}' from Azure Key Vault", secretName);

        var results = new List<SecretVersionInfo>();
        try
        {
            await foreach (SecretProperties p in _client.GetPropertiesOfSecretVersionsAsync(secretName, ct))
            {
                if (!p.Enabled.GetValueOrDefault()) continue;
                results.Add(new SecretVersionInfo(
                    Version: p.Version,
                    CreatedOn: p.CreatedOn,
                    NotBefore: p.NotBefore,
                    ExpiresOn: p.ExpiresOn,
                    Enabled: p.Enabled.GetValueOrDefault()));
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return Array.Empty<SecretVersionInfo>();
        }

        return results
            .OrderByDescending(v => v.CreatedOn ?? DateTimeOffset.MinValue)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            // Attempt to list a single secret to verify connectivity and credentials.
            await foreach (var _ in _client.GetPropertiesOfSecretsAsync(ct))
            {
                break; // Only need to confirm the enumeration starts successfully.
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure Key Vault health check failed");
            return false;
        }
    }
}
