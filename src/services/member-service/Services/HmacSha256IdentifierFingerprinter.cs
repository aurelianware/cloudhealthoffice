using System.Security.Cryptography;
using System.Text;
using CloudHealthOffice.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace MemberService.Services;

/// <summary>
/// HMAC-SHA256 fingerprinter keyed by a secret fetched through
/// <see cref="RotatingKeyProvider"/>. Fingerprint output IS the database
/// lookup key, so the key material can't be part of the output — instead,
/// rotation is handled as dual-read:
///
/// * <see cref="FingerprintAsync"/> returns the fingerprint under the
///   current key version only. Used for writes (inserts / updates of the
///   ValueFingerprint column).
/// * <see cref="FingerprintCandidatesAsync"/> returns one fingerprint per
///   accepted key version. Used for reads (dedupe checks, lookups) so
///   records written under the previous key version still match.
///
/// The fingerprint key MUST be distinct from the AES-GCM encryption key.
/// </summary>
public sealed class HmacSha256IdentifierFingerprinter : IIdentifierFingerprinter
{
    private readonly RotatingKeyProvider _keys;
    private readonly ISecretProvider _secrets;
    private readonly ILogger<HmacSha256IdentifierFingerprinter> _logger;
    private readonly MemberFingerprintingOptions _options;

    public HmacSha256IdentifierFingerprinter(
        RotatingKeyProvider keys,
        ISecretProvider secrets,
        ILogger<HmacSha256IdentifierFingerprinter> logger,
        MemberFingerprintingOptions options)
    {
        _keys = keys;
        _secrets = secrets;
        _logger = logger;
        _options = options;

        if (string.IsNullOrWhiteSpace(options.KeySecretPrefix))
            throw new ArgumentException("KeySecretPrefix is required", nameof(options));
        if (string.IsNullOrWhiteSpace(options.CurrentKeyVersion))
            throw new ArgumentException("CurrentKeyVersion is required", nameof(options));
        if (options.AcceptedKeyVersions.Count == 0)
            throw new ArgumentException("AcceptedKeyVersions must contain at least one entry", nameof(options));
        if (!options.AcceptedKeyVersions.Contains(options.CurrentKeyVersion, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException(
                "CurrentKeyVersion must be listed in AcceptedKeyVersions", nameof(options));
    }

    public bool IsEnabled => true;

    public async Task<string> FingerprintAsync(string normalizedPlaintext, CancellationToken ct = default)
    {
        var key = await ResolveKeyAsync(_options.CurrentKeyVersion, ct);
        return Compute(key, normalizedPlaintext);
    }

    public async Task<IReadOnlyList<string>> FingerprintCandidatesAsync(
        string normalizedPlaintext, CancellationToken ct = default)
    {
        var results = new List<string>(_options.AcceptedKeyVersions.Count);
        foreach (var version in _options.AcceptedKeyVersions)
        {
            byte[] key;
            try
            {
                key = await ResolveKeyAsync(version, ct);
            }
            catch (InvalidOperationException ex)
            {
                // Fail closed: skipping an accepted version means a row
                // fingerprinted under it wouldn't match, and the caller
                // would silently admit a duplicate. That's worse than
                // returning an error. The health check flags this as
                // Degraded so ops has already been paged; the read-path
                // caller should 503 here rather than serve the dedupe
                // check with a partial candidate set.
                _logger.LogError(ex,
                    "Fingerprint key version {Version} listed in AcceptedKeyVersions could not be resolved; failing closed on the candidate set",
                    Sanitize(version));
                throw new StaleFingerprintKeyException(version,
                    $"Accepted fingerprint key version '{version}' cannot be resolved. " +
                    "Publish the secret or drop the version from AcceptedKeyVersions.",
                    ex);
            }
            results.Add(Compute(key, normalizedPlaintext));
        }
        return results;
    }

    private async Task<byte[]> ResolveKeyAsync(string version, CancellationToken ct)
    {
        try
        {
            var key = await _keys.GetKeyAsync(_options.KeySecretPrefix, version, devConfigFallback: null, ct);
            EnsureKeyLength(key);
            return key;
        }
        catch (InvalidOperationException)
        {
            // Fall through to legacy secret name if configured and the
            // version is v1. Lets deployments with only the pre-A.7.3
            // single-name fingerprint key continue resolving candidates
            // under their implicit v1 without publishing a versioned
            // secret on day one.
            if (string.Equals(version, "v1", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(_options.LegacyKeySecretName))
            {
                var raw = await _secrets.GetSecretAsync(_options.LegacyKeySecretName!, ct);
                if (!string.IsNullOrEmpty(raw))
                {
                    var keyBytes = DecodeRaw(raw);
                    EnsureKeyLength(keyBytes);
                    return keyBytes;
                }
            }
            throw;
        }
    }

    private static byte[] DecodeRaw(string raw)
    {
        try { return Convert.FromBase64String(raw); }
        catch (FormatException) { return Encoding.UTF8.GetBytes(raw); }
    }

    private static void EnsureKeyLength(byte[] keyBytes)
    {
        if (keyBytes.Length < 32)
            throw new InvalidOperationException(
                $"Identifier fingerprint HMAC key must be at least 32 bytes; got {keyBytes.Length}.");
    }

    private static string Compute(byte[] key, string normalizedPlaintext)
    {
        var data = Encoding.UTF8.GetBytes(normalizedPlaintext ?? string.Empty);
        var hash = HMACSHA256.HashData(key, data);
        return Convert.ToHexString(hash);
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
