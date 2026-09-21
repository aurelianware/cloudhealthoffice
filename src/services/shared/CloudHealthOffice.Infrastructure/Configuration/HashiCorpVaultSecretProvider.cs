using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Configuration;

/// <summary>
/// Reads secrets from HashiCorp Vault's KV version 2 engine.
/// <para>
/// Speaks Vault's HTTP API directly rather than taking a client library dependency: the surface
/// needed here is four endpoints, and every service in the solution references this assembly, so
/// a new third-party package would be a dependency for all of them.
/// </para>
/// <para>
/// Authentication prefers the Kubernetes auth method, where the pod's projected service account
/// token is exchanged for a Vault token — no long-lived credential is stored anywhere. A static
/// token is accepted for local development and tests.
/// </para>
/// </summary>
public sealed class HashiCorpVaultSecretProvider : ISecretProvider, IDisposable
{
    private const string DefaultMountPoint = "secret";
    private const string DefaultKubernetesAuthPath = "kubernetes";
    private const string DefaultServiceAccountTokenPath =
        "/var/run/secrets/kubernetes.io/serviceaccount/token";

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly SecretProviderOptions _options;
    private readonly ILogger<HashiCorpVaultSecretProvider> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _cachedToken;

    public HashiCorpVaultSecretProvider(
        SecretProviderOptions options,
        ILogger<HashiCorpVaultSecretProvider> logger,
        HttpClient? httpClient = null)
    {
        _options = options;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(options.HashiCorpVaultAddress))
        {
            throw new InvalidOperationException(
                "SecretProvider:HashiCorpVaultAddress must be configured when Provider is " +
                $"{SecretProviderType.HashiCorpVault}.");
        }

        if (string.IsNullOrWhiteSpace(options.HashiCorpVaultKubernetesRole)
            && string.IsNullOrWhiteSpace(options.HashiCorpVaultToken))
        {
            throw new InvalidOperationException(
                "HashiCorp Vault needs either SecretProvider:HashiCorpVaultKubernetesRole " +
                "(preferred) or SecretProvider:HashiCorpVaultToken to authenticate.");
        }

        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient();
        _http.BaseAddress ??= new Uri(options.HashiCorpVaultAddress!.TrimEnd('/') + "/");
    }

    private string MountPoint =>
        string.IsNullOrWhiteSpace(_options.HashiCorpVaultMountPoint)
            ? DefaultMountPoint
            : _options.HashiCorpVaultMountPoint!.Trim('/');

    /// <inheritdoc />
    public async Task<string?> GetSecretAsync(string secretName, CancellationToken ct = default)
        => await ReadSecretAsync(secretName, version: null, ct);

    /// <summary>
    /// KV v2 keeps numbered versions of each secret, so a version is a query parameter on the
    /// same path rather than a separate identifier as in Azure Key Vault.
    /// </summary>
    public async Task<string?> GetSecretByVersionAsync(
        string secretName, string version, CancellationToken ct = default)
        => await ReadSecretAsync(secretName, version, ct);

    private async Task<string?> ReadSecretAsync(string secretName, string? version, CancellationToken ct)
    {
        var path = $"v1/{MountPoint}/data/{Uri.EscapeDataString(secretName)}";
        if (!string.IsNullOrWhiteSpace(version)) path += $"?version={Uri.EscapeDataString(version)}";

        try
        {
            using var response = await SendAsync(HttpMethod.Get, path, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogDebug("Secret '{SecretName}' not found in Vault", secretName);
                return null;
            }

            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<KvReadResponse>(cancellationToken: ct);
            var data = payload?.Data?.Data;
            if (data is null || data.Count == 0) return null;

            // A KV v2 secret is a map. The convention here is a single "value" key; when a secret
            // was written with a different key name and holds only one entry, use that instead so
            // an existing Vault layout does not have to be reshaped.
            if (data.TryGetValue("value", out var value)) return value;

            return data.Count == 1 ? data.Values.First() : null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to read secret '{SecretName}' from Vault", secretName);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, string>> GetSecretsAsync(
        string prefix, CancellationToken ct = default)
    {
        var results = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            using var response = await SendAsync(
                new HttpMethod("LIST"), $"v1/{MountPoint}/metadata", ct);

            if (response.StatusCode == HttpStatusCode.NotFound) return results;
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<KvListResponse>(cancellationToken: ct);
            var keys = payload?.Data?.Keys ?? [];

            // A LIST names nested paths with a trailing slash. Those are folders, not secrets:
            // reading one always 404s, so it would be a wasted round trip and a misleading log
            // line for every subtree.
            foreach (var key in keys.Where(k =>
                         !k.EndsWith('/') && k.StartsWith(prefix, StringComparison.Ordinal)))
            {
                var value = await GetSecretAsync(key, ct);
                if (value is not null) results[key] = value;
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to list secrets with prefix '{Prefix}' from Vault", prefix);
            throw;
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SecretVersionInfo>> ListSecretVersionsAsync(
        string secretName, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendAsync(
                HttpMethod.Get, $"v1/{MountPoint}/metadata/{Uri.EscapeDataString(secretName)}", ct);

            if (response.StatusCode == HttpStatusCode.NotFound) return [];
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<MetadataResponse>(cancellationToken: ct);
            var versions = payload?.Data?.Versions;
            if (versions is null) return [];

            return versions
                .Select(v => new SecretVersionInfo(
                    Version: v.Key,
                    CreatedOn: v.Value.CreatedOn,
                    NotBefore: null,
                    ExpiresOn: v.Value.DeletedOn,
                    // KV v2 marks a version destroyed or soft-deleted rather than disabled.
                    Enabled: !v.Value.Destroyed && v.Value.DeletedOn is null))
                .OrderBy(v => v.Version, StringComparer.Ordinal)
                .ToList();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to list versions of secret '{SecretName}' from Vault", secretName);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            // sys/health needs no token, so this reports on Vault itself rather than on whether
            // this workload's credentials happen to be valid.
            using var request = new HttpRequestMessage(HttpMethod.Get, "v1/sys/health");
            using var response = await _http.SendAsync(request, ct);

            // Vault answers 200 when unsealed and active, and uses other 2xx/4xx codes for standby
            // or sealed states; only an unsealed active node can serve reads.
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Vault health check failed");
            return false;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, CancellationToken ct)
    {
        var token = await GetTokenAsync(ct);

        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("X-Vault-Token", token);

        var response = await _http.SendAsync(request, ct);

        // A Vault token expires. Re-authenticate once and retry rather than surfacing a 403 that
        // only means the lease ran out.
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
            && !string.IsNullOrWhiteSpace(_options.HashiCorpVaultKubernetesRole))
        {
            response.Dispose();
            InvalidateToken();

            var refreshed = await GetTokenAsync(ct);
            using var retry = new HttpRequestMessage(method, path);
            retry.Headers.TryAddWithoutValidation("X-Vault-Token", refreshed);
            return await _http.SendAsync(retry, ct);
        }

        return response;
    }

    /// <summary>
    /// Clears the cached token. Deliberately lock-free: this is a single reference write, and
    /// taking the semaphore synchronously from the async request path would block a thread pool
    /// thread. A racing GetTokenAsync simply re-authenticates, which is the intended outcome.
    /// </summary>
    private void InvalidateToken() => Volatile.Write(ref _cachedToken, null);

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_options.HashiCorpVaultToken))
        {
            return _options.HashiCorpVaultToken!;
        }

        var cached = Volatile.Read(ref _cachedToken);
        if (cached is not null) return cached;

        await _tokenLock.WaitAsync(ct);
        try
        {
            cached = Volatile.Read(ref _cachedToken);
            if (cached is not null) return cached;

            var tokenPath = string.IsNullOrWhiteSpace(_options.HashiCorpVaultServiceAccountTokenPath)
                ? DefaultServiceAccountTokenPath
                : _options.HashiCorpVaultServiceAccountTokenPath!;

            if (!File.Exists(tokenPath))
            {
                throw new InvalidOperationException(
                    $"Kubernetes service account token not found at '{tokenPath}'. Vault Kubernetes " +
                    "auth only works inside a pod; set SecretProvider:HashiCorpVaultToken for " +
                    "local use.");
            }

            var jwt = (await File.ReadAllTextAsync(tokenPath, ct)).Trim();
            var authPath = string.IsNullOrWhiteSpace(_options.HashiCorpVaultKubernetesAuthPath)
                ? DefaultKubernetesAuthPath
                : _options.HashiCorpVaultKubernetesAuthPath!.Trim('/');

            using var response = await _http.PostAsJsonAsync(
                $"v1/auth/{authPath}/login",
                new { role = _options.HashiCorpVaultKubernetesRole, jwt },
                ct);

            response.EnsureSuccessStatusCode();

            var login = await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: ct);
            var clientToken = login?.Auth?.ClientToken;

            if (string.IsNullOrWhiteSpace(clientToken))
            {
                throw new InvalidOperationException(
                    "Vault Kubernetes login succeeded but returned no client token.");
            }

            Volatile.Write(ref _cachedToken, clientToken);
            return clientToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public void Dispose()
    {
        _tokenLock.Dispose();
        if (_ownsHttpClient) _http.Dispose();
    }

    private sealed record KvReadResponse([property: JsonPropertyName("data")] KvReadData? Data);

    private sealed record KvReadData(
        [property: JsonPropertyName("data")] Dictionary<string, string>? Data);

    private sealed record KvListResponse([property: JsonPropertyName("data")] KvListData? Data);

    private sealed record KvListData([property: JsonPropertyName("keys")] List<string>? Keys);

    private sealed record MetadataResponse([property: JsonPropertyName("data")] MetadataData? Data);

    private sealed record MetadataData(
        [property: JsonPropertyName("versions")] Dictionary<string, MetadataVersion>? Versions);

    /// <summary>
    /// Vault sends timestamps as strings and uses an empty string, not null, for a version that
    /// has never been deleted — which will not bind to a DateTimeOffset?. They are read as strings
    /// and parsed here.
    /// </summary>
    private sealed record MetadataVersion(
        [property: JsonPropertyName("created_time")] string? CreatedTime,
        [property: JsonPropertyName("deletion_time")] string? DeletionTime,
        [property: JsonPropertyName("destroyed")] bool Destroyed)
    {
        public DateTimeOffset? CreatedOn => Parse(CreatedTime);

        public DateTimeOffset? DeletedOn => Parse(DeletionTime);

        private static DateTimeOffset? Parse(string? value)
            => DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private sealed record LoginResponse([property: JsonPropertyName("auth")] LoginAuth? Auth);

    private sealed record LoginAuth(
        [property: JsonPropertyName("client_token")] string? ClientToken);
}
