using CloudHealthOffice.Infrastructure.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CloudHealthOffice.Infrastructure.HealthChecks;

/// <summary>
/// Verifies that every (secretPrefix, versions) pair a host service cares
/// about resolves via the registered <see cref="RotatingKeyProvider"/>.
/// A service configured with AcceptedKeyVersions including a legacy
/// version whose secret was accidentally deleted from Key Vault must
/// surface that as a health signal BEFORE the next rotation-window read.
///
/// Reports <see cref="HealthStatus.Degraded"/> (not unhealthy) on missing
/// versions — a missing legacy version is a compliance / operator concern,
/// not a serve-5xx concern.
/// </summary>
public sealed class RotatingKeyProviderHealthCheck : IHealthCheck
{
    private readonly RotatingKeyProvider _keys;
    private readonly IReadOnlyList<RotatingKeyVersionProbe> _probes;

    public RotatingKeyProviderHealthCheck(
        RotatingKeyProvider keys,
        IEnumerable<RotatingKeyVersionProbe> probes)
    {
        _keys = keys;
        _probes = probes.ToList();
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var missing = new List<string>();
        foreach (var probe in _probes)
        {
            foreach (var version in probe.Versions)
            {
                try
                {
                    _ = await _keys.GetKeyAsync(probe.SecretPrefix, version, probe.DevConfigFallback, cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    missing.Add($"{probe.SecretPrefix}-{version}");
                }
            }
        }

        if (missing.Count == 0)
            return HealthCheckResult.Healthy(
                $"All {_probes.Sum(p => p.Versions.Count)} rotating key version(s) resolved.");

        return HealthCheckResult.Degraded(
            $"Unresolvable rotating key version(s): {string.Join(", ", missing)}. " +
            "Either publish the secret or remove the version from AcceptedKeyVersions.");
    }
}

/// <summary>
/// One (secret prefix, accepted versions) pair to probe during health
/// checks. Host services register one probe per rotating-key consumer
/// they own (encryptor, fingerprinter, QR signer, etc.).
/// </summary>
public sealed record RotatingKeyVersionProbe(
    string SecretPrefix,
    IReadOnlyList<string> Versions,
    string? DevConfigFallback = null);
