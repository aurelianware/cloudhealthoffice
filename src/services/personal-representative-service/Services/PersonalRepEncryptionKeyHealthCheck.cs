using CloudHealthOffice.Infrastructure.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PersonalRepresentativeService.Services;

/// <summary>
/// Readiness check: verify that the CURRENT Personal Rep encryption key can
/// be resolved from the secret provider. A missing current key means new
/// rep writes will fail at encrypt time; surfacing this as a 503 on
/// <c>/health/ready</c> keeps the service from being rotated into traffic
/// with a broken encryption path.
///
/// Local to personal-representative-service for now. Consent-service is
/// consumer #2 of RotatingKeyProvider; this is #3. Promotion to shared
/// infra happens when a fourth service needs the same pattern.
/// </summary>
public sealed class PersonalRepEncryptionKeyHealthCheck : IHealthCheck
{
    private readonly RotatingKeyProvider _keys;
    private readonly PersonalRepEncryptionOptions _options;

    public PersonalRepEncryptionKeyHealthCheck(RotatingKeyProvider keys, PersonalRepEncryptionOptions options)
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
                    $"Personal Rep encryption key '{_options.KeySecretPrefix}-{_options.CurrentKeyVersion}' must be 32 bytes (AES-256); got {key.Length}.");
            }

            return HealthCheckResult.Healthy(
                $"Current Personal Rep encryption key '{_options.CurrentKeyVersion}' resolved.");
        }
        catch (InvalidOperationException ex)
        {
            return HealthCheckResult.Unhealthy(
                $"Current Personal Rep encryption key '{_options.CurrentKeyVersion}' not resolvable from secret provider.",
                ex);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                "Unexpected error resolving Personal Rep encryption key.",
                ex);
        }
    }
}
