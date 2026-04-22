namespace CloudHealthOffice.Infrastructure.Configuration;

/// <summary>
/// Abstraction for retrieving application secrets from a configured secret store.
/// Implementations include Azure Key Vault, HashiCorp Vault, and a null (no-op) provider.
/// </summary>
public interface ISecretProvider
{
    /// <summary>
    /// Retrieves a single secret value by name.
    /// </summary>
    /// <param name="secretName">The name (or key) of the secret to retrieve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The secret value, or <c>null</c> if the secret does not exist.</returns>
    Task<string?> GetSecretAsync(string secretName, CancellationToken ct = default);

    /// <summary>
    /// Retrieves all secrets whose names start with the specified prefix.
    /// </summary>
    /// <param name="prefix">
    /// The prefix to filter secret names by. Use an empty string to retrieve all secrets.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A dictionary of secret names to their values. Empty if none match.</returns>
    Task<IDictionary<string, string>> GetSecretsAsync(string prefix, CancellationToken ct = default);

    /// <summary>
    /// Checks whether the underlying secret store is reachable and operational.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if the secret store is healthy; otherwise <c>false</c>.</returns>
    Task<bool> HealthCheckAsync(CancellationToken ct = default);

    /// <summary>
    /// Retrieves a specific version of a secret. Version identifiers are
    /// provider-specific — for Azure Key Vault, this is the URL segment
    /// after the secret name (e.g. "abc123..."). Returns null if the
    /// secret or version does not exist.
    /// </summary>
    Task<string?> GetSecretByVersionAsync(
        string secretName,
        string version,
        CancellationToken ct = default);

    /// <summary>
    /// Returns metadata about every enabled version of the named secret,
    /// ordered by creation time descending (newest first). Empty if the
    /// secret has no enabled versions.
    /// </summary>
    Task<IReadOnlyList<SecretVersionInfo>> ListSecretVersionsAsync(
        string secretName,
        CancellationToken ct = default);
}
