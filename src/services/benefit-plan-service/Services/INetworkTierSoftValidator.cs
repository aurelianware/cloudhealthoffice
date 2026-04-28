using BenefitPlanService.Models;
using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.Extensions.Options;

namespace BenefitPlanService.Services;

/// <summary>
/// Soft-validation gate (capability 5.5 — NetworkTier as Reference to
/// Organization). Walks the <see cref="BenefitPlan.NetworkTiers"/>
/// collection on every write surface and records a structured warning
/// plus a Prometheus counter increment for each tier that lacks
/// <see cref="NetworkTier.NetworkId"/>.
///
/// <para>
/// Mirrors the soft-validation pattern from provider-service capability
/// 5.5 (<c>IPanelGatingValidator</c>): writes still succeed; the
/// counter drives the eventual hard-validation cutover. When
/// <c>cho.benefit_plan.network_tier_missing_networkid_writes.total</c>
/// reads zero across all tenants for a sustained window the follow-up
/// PR flips this from soft to hard validation (400 rejection).
/// </para>
/// </summary>
public interface INetworkTierSoftValidator
{
    /// <summary>
    /// Inspect <paramref name="plan"/> and emit a warning + counter
    /// increment for each <see cref="NetworkTier"/> that has a null or
    /// empty <see cref="NetworkTier.NetworkId"/>. The
    /// <paramref name="caller"/> label distinguishes write surfaces in
    /// telemetry.
    /// </summary>
    void Inspect(BenefitPlan plan, NetworkTierWriteCaller caller);
}

/// <summary>
/// Write-surface labels for the soft-validation counter dimension
/// <c>cho.caller</c>. New write surfaces must add a label here so the
/// counter never carries an unbounded set of values.
/// </summary>
public enum NetworkTierWriteCaller
{
    CreatePlan,
    UpdatePlan,
    CreateDraft,
    AmendPublished,
    PublishAndSupersede,
}

public sealed class NetworkTierSoftValidator : INetworkTierSoftValidator
{
    private readonly IOptionsMonitor<NetworkTierBackfillOptions> _options;
    private readonly ILogger<NetworkTierSoftValidator> _logger;

    public NetworkTierSoftValidator(
        IOptionsMonitor<NetworkTierBackfillOptions> options,
        ILogger<NetworkTierSoftValidator> logger)
    {
        _options = options;
        _logger = logger;
    }

    public void Inspect(BenefitPlan plan, NetworkTierWriteCaller caller)
    {
        if (plan?.NetworkTiers is null || plan.NetworkTiers.Count == 0) return;

        var level = _options.CurrentValue.SoftValidationLogLevel;
        for (var i = 0; i < plan.NetworkTiers.Count; i++)
        {
            var tier = plan.NetworkTiers[i];
            if (!string.IsNullOrEmpty(tier?.NetworkId)) continue;

            ChoMetrics.NetworkTierMissingNetworkIdWrites.Add(
                1,
                new KeyValuePair<string, object?>("cho.caller", caller.ToString()),
                new KeyValuePair<string, object?>("cho.tenant_id", plan.TenantId ?? string.Empty));

            // Structured single-line warning per offending tier. Fields
            // mirror the panel-gating soft-validation shape from
            // provider-service 5.5 so dashboards can be templated across
            // both signals.
            _logger.Log(
                level,
                "NetworkTierNetworkIdMissing on plan write. caller={Caller} tenantId={TenantId} planId={PlanId} versionId={VersionId} tierIndex={TierIndex} tierName={TierName} tierLevel={TierLevel}",
                caller,
                SanitizeForLog(plan.TenantId),
                SanitizeForLog(plan.PlanId),
                SanitizeForLog(plan.VersionId),
                i,
                SanitizeForLog(tier?.TierName ?? string.Empty),
                tier?.TierLevel ?? 0);
        }
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
