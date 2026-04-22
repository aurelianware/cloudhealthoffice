namespace CloudHealthOffice.Infrastructure.Configuration;

/// <summary>
/// A no-op <see cref="ISecretProvider"/> used when <see cref="SecretProviderType.None"/> is configured.
/// Returns <c>null</c> or empty results for all operations so that microservices can start
/// without a secret store configured.
/// </summary>
public sealed class NullSecretProvider : ISecretProvider
{
    /// <inheritdoc />
    public Task<string?> GetSecretAsync(string secretName, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    /// <inheritdoc />
    public Task<IDictionary<string, string>> GetSecretsAsync(string prefix, CancellationToken ct = default)
        => Task.FromResult<IDictionary<string, string>>(new Dictionary<string, string>());

    /// <inheritdoc />
    public Task<bool> HealthCheckAsync(CancellationToken ct = default)
        => Task.FromResult(true);

    /// <inheritdoc />
    public Task<string?> GetSecretByVersionAsync(
        string secretName, string version, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    /// <inheritdoc />
    public Task<IReadOnlyList<SecretVersionInfo>> ListSecretVersionsAsync(
        string secretName, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SecretVersionInfo>>(Array.Empty<SecretVersionInfo>());
}
