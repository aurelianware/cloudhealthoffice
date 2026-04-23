using CloudHealthOffice.Infrastructure.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AppealsService.Services;

/// <summary>
/// Readiness check: verify that the CURRENT appeal encryption key can be
/// resolved from the secret provider. A missing current key means new
/// appeal writes will fail at encrypt time; surfacing this as a 503 on
/// <c>/health/ready</c> keeps the service from being rotated into traffic
/// with a broken encryption path.
///
/// Local to appeals-service for now. No shared-infra refactor in this PR —
/// when a fifth service needs the same check (after member, consent,
/// personal-rep, appeals), that's the forcing function for promotion.
/// </summary>
public sealed class AppealEncryptionKeyHealthCheck : IHealthCheck
{
    private readonly RotatingKeyProvider _keys;
    private readonly AppealEncryptionOptions _options;

    public AppealEncryptionKeyHealthCheck(RotatingKeyProvider keys, AppealEncryptionOptions options)
    {
        _keys = keys;
        _options = options;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var key = await _keys.GetKeyAsync(
                _options.KeySecretPrefix,
                _options.CurrentKeyVersion,
                devConfigFallback: null,
                cancellationToken);

            if (key.Length != 32)
            {
                return HealthCheckResult.Unhealthy(
                    $"Appeal encryption key '{_options.KeySecretPrefix}-{_options.CurrentKeyVersion}' must be 32 bytes (AES-256); got {key.Length}.");
            }

            return HealthCheckResult.Healthy(
                $"Current appeal encryption key '{_options.CurrentKeyVersion}' resolved.");
        }
        catch (InvalidOperationException ex)
        {
            return HealthCheckResult.Unhealthy(
                $"Current appeal encryption key '{_options.CurrentKeyVersion}' not resolvable from secret provider.",
                ex);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                "Unexpected error resolving appeal encryption key.",
                ex);
        }
    }
}
