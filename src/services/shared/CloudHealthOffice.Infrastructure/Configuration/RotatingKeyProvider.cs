using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Configuration;

/// <summary>
/// Rotation-aware view of a named secret. Keys are identified by an
/// operator-controlled version string (e.g. "v1", "v2") rather than an
/// opaque provider version ID, so rotation is the ops operation of
/// "publish {secretPrefix}-v2 and update CurrentKeyVersion" rather than a
/// code change. Resolved keys are cached in-process; the cache is flushed
/// by <see cref="InvalidateCache"/>, which is driven by the
/// <see cref="SecretRefreshService"/> off the IConfiguration reload token
/// fired by <see cref="SecretProviderConfigurationProvider"/>.
/// </summary>
/// <remarks>
/// Intended for symmetric key material only (HMAC, AES-GCM data
/// encryption keys). Asymmetric key rotation (RS256 signing, TLS certs)
/// has its own JWKS / cert-rotation patterns and is out of scope.
///
/// The class is not sealed so tests can inject a subclass that overrides
/// <see cref="InvalidateCache"/>; only that method is virtual.
/// </remarks>
public class RotatingKeyProvider
{
    private readonly ISecretProvider _secrets;
    private readonly ILogger<RotatingKeyProvider> _logger;
    private readonly ConcurrentDictionary<CacheKey, byte[]> _cache = new();
    private readonly SemaphoreSlim _resolveLock = new(1, 1);

    public RotatingKeyProvider(ISecretProvider secrets, ILogger<RotatingKeyProvider> logger)
    {
        _secrets = secrets;
        _logger = logger;
    }

    /// <summary>
    /// Resolve the key bytes for a specific logical version of a named
    /// secret. Secret name convention: "{prefix}-{version}".
    /// Throws <see cref="InvalidOperationException"/> if the secret is absent
    /// and no <paramref name="devConfigFallback"/> is supplied.
    /// </summary>
    public async Task<byte[]> GetKeyAsync(
        string secretPrefix,
        string version,
        string? devConfigFallback = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(secretPrefix))
            throw new ArgumentException("secretPrefix is required", nameof(secretPrefix));
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("version is required", nameof(version));

        var cacheKey = new CacheKey(secretPrefix, version);
        if (_cache.TryGetValue(cacheKey, out var cached)) return cached;

        await _resolveLock.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(cacheKey, out cached)) return cached;

            var secretName = $"{secretPrefix}-{version}";
            var raw = await _secrets.GetSecretAsync(secretName, ct);

            if (string.IsNullOrEmpty(raw))
                raw = devConfigFallback;

            if (string.IsNullOrEmpty(raw))
                throw new InvalidOperationException(
                    $"Rotating key '{secretName}' is not configured. " +
                    "Publish the secret and/or update AcceptedKeyVersions.");

            byte[] keyBytes;
            try
            {
                keyBytes = Convert.FromBase64String(raw);
            }
            catch (FormatException)
            {
                // Accept UTF-8 strings in dev environments; production secrets
                // should be stored base64-encoded random bytes.
                keyBytes = Encoding.UTF8.GetBytes(raw);
            }

            _cache[cacheKey] = keyBytes;
            return keyBytes;
        }
        finally
        {
            _resolveLock.Release();
        }
    }

    /// <summary>
    /// Clear the entire in-process cache. Called by
    /// <see cref="SecretRefreshService"/> when <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>
    /// fires a reload token so the next sign/verify picks up newly-loaded
    /// secret values. Logged at Information so ops has an audit trail that
    /// rotation propagated.
    /// </summary>
    public virtual void InvalidateCache()
    {
        var dropped = _cache.Count;
        _cache.Clear();
        _logger.LogInformation(
            "RotatingKeyProvider cache invalidated ({Count} keys dropped) in response to IConfiguration reload.",
            dropped);
    }

    private readonly record struct CacheKey(string Prefix, string Version)
    {
        public bool Equals(CacheKey other) =>
            string.Equals(Prefix, other.Prefix, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Version, other.Version, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(Prefix),
            StringComparer.OrdinalIgnoreCase.GetHashCode(Version));
    }
}
