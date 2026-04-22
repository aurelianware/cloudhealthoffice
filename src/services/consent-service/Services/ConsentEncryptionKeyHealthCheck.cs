using CloudHealthOffice.Infrastructure.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ConsentService.Services;

/// <summary>
/// Readiness check: verify that the CURRENT consent encryption key can be
/// resolved from the secret provider. A missing current key means new
/// consent writes will fail at encrypt time; surfacing this as a 503 on
/// <c>/health/ready</c> keeps the service from being rotated into traffic
/// with a broken encryption path.
///
/// Local to consent-service for now. No shared-infra refactor in this PR —
/// when a second service needs the same check, that's the forcing function
/// for promotion.
/// </summary>
public sealed class ConsentEncryptionKeyHealthCheck : IHealthCheck
{
    private readonly RotatingKeyProvider _keys;
    private readonly ConsentEncryptionOptions _options;

    public ConsentEncryptionKeyHealthCheck(RotatingKeyProvider keys, ConsentEncryptionOptions options)
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
                    $"Consent encryption key '{_options.KeySecretPrefix}-{_options.CurrentKeyVersion}' must be 32 bytes (AES-256); got {key.Length}.");
            }

            return HealthCheckResult.Healthy(
                $"Current consent encryption key '{_options.CurrentKeyVersion}' resolved.");
        }
        catch (InvalidOperationException ex)
        {
            return HealthCheckResult.Unhealthy(
                $"Current consent encryption key '{_options.CurrentKeyVersion}' not resolvable from secret provider.",
                ex);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                "Unexpected error resolving consent encryption key.",
                ex);
        }
    }
}
