using Microsoft.Extensions.Configuration;

namespace CloudHealthOffice.Infrastructure.Configuration;

/// <summary>
/// Resolves secrets from <see cref="IConfiguration"/> under the <c>Secrets</c> section.
/// <para>
/// <b>For local development and tests only.</b> Values come from configuration — environment
/// variables, appsettings, or a Kubernetes Secret mounted as env vars — so they are visible to
/// anything that can read the process environment. Never select this provider for an environment
/// holding real PHI or production keys; use <see cref="SecretProviderType.AzureKeyVault"/> there.
/// </para>
/// <para>
/// It exists because the alternatives cannot run outside Azure: Azure Key Vault needs a real
/// vault and workload identity, and <see cref="NullSecretProvider"/> resolves nothing, which
/// leaves services that require a rotating encryption key permanently unready on a local cluster.
/// </para>
/// <example>
/// <code>
/// SecretProvider__Provider=Configuration
/// Secrets__appeal-body-encryption-key-v1=&lt;base64 32 bytes&gt;
/// </code>
/// </example>
/// </summary>
public sealed class ConfigurationSecretProvider : ISecretProvider
{
    /// <summary>Configuration section holding secret values, keyed by secret name.</summary>
    public const string SectionName = "Secrets";

    private readonly IConfiguration _configuration;

    public ConfigurationSecretProvider(IConfiguration configuration)
        => _configuration = configuration;

    /// <summary>
    /// Looks the secret up by name, then by the same name with hyphens replaced by underscores.
    /// <para>
    /// Secret names follow a <c>{prefix}-{version}</c> convention, but Kubernetes requires
    /// environment variable names to be C identifiers, so a hyphenated name cannot be injected as
    /// one — kubectl drops it silently. The underscore form is what callers can actually set.
    /// </para>
    /// </summary>
    public Task<string?> GetSecretAsync(string secretName, CancellationToken ct = default)
    {
        var value = _configuration[$"{SectionName}:{secretName}"];

        if (string.IsNullOrEmpty(value) && secretName.Contains('-'))
        {
            value = _configuration[$"{SectionName}:{secretName.Replace('-', '_')}"];
        }

        return Task.FromResult(value);
    }

    /// <inheritdoc />
    public Task<IDictionary<string, string>> GetSecretsAsync(string prefix, CancellationToken ct = default)
    {
        IDictionary<string, string> results = _configuration
            .GetSection(SectionName)
            .GetChildren()
            .Where(c => c.Key.StartsWith(prefix, StringComparison.Ordinal) && c.Value is not null)
            .ToDictionary(c => c.Key, c => c.Value!, StringComparer.Ordinal);

        return Task.FromResult(results);
    }

    /// <inheritdoc />
    public Task<bool> HealthCheckAsync(CancellationToken ct = default)
        => Task.FromResult(true);

    /// <summary>
    /// Configuration holds one value per secret name, so a version is only resolvable when it is
    /// part of the name, matching the <c>{prefix}-{version}</c> convention callers already use.
    /// </summary>
    public Task<string?> GetSecretByVersionAsync(
        string secretName, string version, CancellationToken ct = default)
        => GetSecretAsync($"{secretName}-{version}", ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<SecretVersionInfo>> ListSecretVersionsAsync(
        string secretName, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SecretVersionInfo>>(Array.Empty<SecretVersionInfo>());
}
