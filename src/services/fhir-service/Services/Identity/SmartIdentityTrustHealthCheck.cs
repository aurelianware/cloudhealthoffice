using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FhirService.Services.Identity;

/// <summary>
/// Reports whether CHO can currently establish trust — configuration is valid,
/// and every trusted issuer's signing keys have been retrieved.
///
/// It reports OPERATIONAL health, not token outcomes. A flood of 401s because
/// clients are presenting expired tokens is a healthy resource server doing its
/// job; an issuer whose keys have never loaded is a resource server that will
/// reject every token no matter who presents it. Conflating the two would make
/// this signal fire during an attack and stay silent during an outage.
///
/// Nothing key-shaped is exposed: issuer names, key COUNTS, retrieval times and
/// a failure category. No keys, no discovery payload, no secrets — a health
/// endpoint is frequently the least-authenticated surface a service has.
/// </summary>
public sealed class SmartIdentityTrustHealthCheck : IHealthCheck
{
    private readonly SmartSigningKeyRing _keyRing;
    private readonly TrustedIssuerRegistry _registry;

    public SmartIdentityTrustHealthCheck(SmartSigningKeyRing keyRing, TrustedIssuerRegistry registry)
    {
        _keyRing = keyRing;
        _registry = registry;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var statuses = _keyRing.Status();

        var data = new Dictionary<string, object>
        {
            ["mode"] = _registry.Mode.ToString(),
            ["trustedIssuers"] = statuses.Count,
            ["issuersWithKeys"] = statuses.Count(s => s.HasKeys),
        };

        foreach (var status in statuses)
        {
            data[$"issuer:{status.Issuer}"] = status.HasKeys
                ? $"{status.KeyCount} key(s), retrieved {status.RetrievedAtUtc:o}"
                  + (status.IsStale ? ", stale" : string.Empty)
                : $"no keys ({Describe(status.LastError)})";
        }

        if (statuses.Count == 0)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "No trusted issuers are configured; no token can be validated.", data: data));
        }

        var withoutKeys = statuses.Where(s => !s.HasKeys).ToList();
        if (withoutKeys.Count == statuses.Count)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "No trusted issuer has usable signing keys; authentication is unavailable.",
                data: data));
        }

        if (withoutKeys.Count > 0)
        {
            // Some issuers work, some do not: callers of the healthy issuers are
            // served normally, so this is degraded rather than unhealthy —
            // pulling the instance out of rotation would not fix the broken one.
            return Task.FromResult(HealthCheckResult.Degraded(
                $"{withoutKeys.Count} of {statuses.Count} trusted issuers have no usable signing keys.",
                data: data));
        }

        if (statuses.Any(s => s.IsStale))
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                "Signing keys are past their refresh interval but still within the staleness bound.",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            $"{statuses.Count} trusted issuer(s) with current signing keys.", data: data));
    }

    /// <summary>
    /// A failure category, not the underlying message. An IdP's error text can
    /// carry hostnames and request identifiers that do not belong on a health
    /// endpoint.
    /// </summary>
    private static string Describe(string? error)
        => string.IsNullOrEmpty(error) ? "not yet retrieved" : "retrieval failed";
}

/// <summary>
/// Retrieves every trusted issuer's keys once at startup, so the first real
/// request does not pay for discovery and readiness reflects true trust state
/// rather than "nothing has been tried yet".
///
/// Deliberately non-fatal: an IdP that is briefly unreachable while CHO starts
/// should leave the instance reporting unready, not crash-loop it. Configuration
/// errors already failed startup back in <see cref="SmartTrustOptions.Validate"/>;
/// this is the network, which is allowed to be temporarily unavailable.
/// </summary>
public sealed class SmartTrustWarmupHostedService : IHostedService
{
    private readonly SmartSigningKeyRing _keyRing;
    private readonly TrustedIssuerRegistry _registry;
    private readonly ILogger<SmartTrustWarmupHostedService> _logger;

    public SmartTrustWarmupHostedService(
        SmartSigningKeyRing keyRing,
        TrustedIssuerRegistry registry,
        ILogger<SmartTrustWarmupHostedService> logger)
    {
        _keyRing = keyRing;
        _registry = registry;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var issuer in _registry.Issuers)
        {
            var primed = await _keyRing.TryPrimeAsync(issuer.Issuer, cancellationToken);
            if (!primed)
            {
                _logger.LogWarning(
                    "Could not retrieve signing keys for trusted issuer {Issuer} at startup; "
                    + "readiness will report this until retrieval succeeds.",
                    issuer.Issuer.Replace("\r", string.Empty).Replace("\n", string.Empty));
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
