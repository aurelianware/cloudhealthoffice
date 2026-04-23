using CloudHealthOffice.Infrastructure.Configuration;

namespace AppealsService.Tests.Fakes;

/// <summary>
/// In-memory <see cref="ISecretProvider"/> backed by a mutable dictionary.
/// Tests add / remove entries to simulate key publication and rotation.
/// Verbatim copy of the consent-service / personal-rep-service shape.
/// </summary>
public sealed class DictionarySecretProvider : ISecretProvider
{
    public Dictionary<string, string> Secrets { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<string?> GetSecretAsync(string secretName, CancellationToken ct = default)
    {
        Secrets.TryGetValue(secretName, out var v);
        return Task.FromResult<string?>(v);
    }

    public Task<IDictionary<string, string>> GetSecretsAsync(string prefix, CancellationToken ct = default)
    {
        IDictionary<string, string> filtered = Secrets
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        return Task.FromResult(filtered);
    }

    public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task<string?> GetSecretByVersionAsync(string secretName, string version, CancellationToken ct = default)
        => GetSecretAsync(secretName, ct);

    public Task<IReadOnlyList<SecretVersionInfo>> ListSecretVersionsAsync(string secretName, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SecretVersionInfo>>(Array.Empty<SecretVersionInfo>());
}
