using BenefitPlanService.Models;
using CloudHealthOffice.Infrastructure.Observability;

namespace BenefitPlanService.Services;

/// <summary>
/// Hard-validation gate for plan-level cost-sharing limits against ACA
/// 45 CFR §156.130 caps (capability BP 5.7). Wired into all five
/// <see cref="BenefitPlanServiceImpl"/> write surfaces so a noncompliant
/// plan cannot land in the store regardless of which API path the
/// operator used.
///
/// <para>
/// Throws <see cref="PlanLimitValidationException"/> on violation;
/// controllers map to <b>400 Bad Request</b>. Unlike
/// <see cref="INetworkTierSoftValidator"/> (counter + log only), this
/// validator is non-negotiable: regulatory caps are the floor, not a
/// migration target.
/// </para>
///
/// <para>
/// <b>Both modes validated.</b> Embedded plans must satisfy
/// <c>IndividualOutOfPocketMax ≤ acaIndividualCap</c>; Aggregate plans
/// must satisfy <c>FamilyOutOfPocketMax ≤ acaFamilyCap</c>. Both checks
/// run against the plan-year resolved by
/// <see cref="IPlanYearResolver"/>.
/// </para>
/// </summary>
public interface IPlanLimitValidator
{
    void Validate(BenefitPlan plan, PlanLimitWriteCaller caller);
}

/// <summary>
/// Write-surface labels for the validator's telemetry counter dimension
/// <c>cho.caller</c>. New write surfaces must add a label here so the
/// counter never carries an unbounded set of values.
/// </summary>
public enum PlanLimitWriteCaller
{
    CreatePlan,
    UpdatePlan,
    CreateDraft,
    AmendPublished,
    PublishAndSupersede,
}

public sealed class PlanLimitValidator : IPlanLimitValidator
{
    private readonly IAcaLimitsProvider _limits;
    private readonly IPlanYearResolver _planYear;
    private readonly ILogger<PlanLimitValidator> _logger;

    public PlanLimitValidator(
        IAcaLimitsProvider limits,
        IPlanYearResolver planYear,
        ILogger<PlanLimitValidator> logger)
    {
        _limits = limits;
        _planYear = planYear;
        _logger = logger;
    }

    public void Validate(BenefitPlan plan, PlanLimitWriteCaller caller)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));

        var planYear = _planYear.Resolve(plan);
        var caps = _limits.GetForPlanYear(planYear);
        if (caps is null)
        {
            // Fail-closed: better to reject the plan with a clear pointer
            // at the missing config than silently accept a plan against
            // an absent cap (G3).
            ChoMetrics.PlanLimitValidationFailures.Add(
                1,
                new KeyValuePair<string, object?>("cho.caller", caller.ToString()),
                new KeyValuePair<string, object?>("cho.tenant_id", plan.TenantId ?? string.Empty),
                new KeyValuePair<string, object?>("cho.reason", "PlanYearNotConfigured"));

            var configured = string.Join(", ", _limits.ConfiguredPlanYears.OrderBy(y => y));
            throw new PlanLimitValidationException(
                plan.PlanId,
                plan.VersionId,
                planYear,
                field: "planYear",
                message: $"ACA OOP limits not configured for plan year {planYear}. " +
                         $"Configured years: [{configured}]. Update schemas/aca-oop-limits/limits.json " +
                         $"or correct the plan's effective / plan-year-definition fields.",
                supplied: planYear,
                cap: 0);
        }

        var cs = plan.CostSharing ?? new CostSharing();

        // Individual OOP cap — applies to BOTH modes. In Embedded mode this
        // is the per-member cap directly. In Aggregate mode the equivalent
        // per-member cap is the ACA cap itself, so the existing
        // IndividualOutOfPocketMax field is treated advisory; if set, it
        // still cannot exceed the ACA ceiling.
        var individualOop = cs.IndividualOutOfPocketMax;
        if (individualOop > 0 && individualOop > caps.IndividualCap)
        {
            ChoMetrics.PlanLimitValidationFailures.Add(
                1,
                new KeyValuePair<string, object?>("cho.caller", caller.ToString()),
                new KeyValuePair<string, object?>("cho.tenant_id", plan.TenantId ?? string.Empty),
                new KeyValuePair<string, object?>("cho.reason", "IndividualOopExceedsAcaCap"));

            throw new PlanLimitValidationException(
                plan.PlanId,
                plan.VersionId,
                planYear,
                field: "costSharing.individualOutOfPocketMax",
                message: $"IndividualOutOfPocketMax ({individualOop:C0}) exceeds the ACA " +
                         $"§156.130 individual cap ({caps.IndividualCap:C0}) for plan year {planYear}.",
                supplied: individualOop,
                cap: caps.IndividualCap);
        }

        // Family OOP cap — applies to BOTH modes (G6).
        var familyOop = cs.FamilyOutOfPocketMax;
        if (familyOop > 0 && familyOop > caps.FamilyCap)
        {
            ChoMetrics.PlanLimitValidationFailures.Add(
                1,
                new KeyValuePair<string, object?>("cho.caller", caller.ToString()),
                new KeyValuePair<string, object?>("cho.tenant_id", plan.TenantId ?? string.Empty),
                new KeyValuePair<string, object?>("cho.reason", "FamilyOopExceedsAcaCap"));

            throw new PlanLimitValidationException(
                plan.PlanId,
                plan.VersionId,
                planYear,
                field: "costSharing.familyOutOfPocketMax",
                message: $"FamilyOutOfPocketMax ({familyOop:C0}) exceeds the ACA " +
                         $"§156.130 family cap ({caps.FamilyCap:C0}) for plan year {planYear}.",
                supplied: familyOop,
                cap: caps.FamilyCap);
        }

        _logger.LogDebug(
            "PlanLimitValidator passed for plan {PlanId} version {VersionId} caller={Caller} planYear={PlanYear}",
            SanitizeForLog(plan.PlanId),
            SanitizeForLog(plan.VersionId),
            caller,
            planYear);
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

/// <summary>
/// Thrown when a plan's cost-sharing limits violate ACA §156.130 or the
/// plan year is not configured. Mapped to HTTP 400 by the controllers.
/// Distinct from <see cref="PlanVersionStateException"/> (which maps to
/// 409) so error-handling paths stay clean.
/// </summary>
public sealed class PlanLimitValidationException : Exception
{
    public string PlanId { get; }
    public string VersionId { get; }
    public int PlanYear { get; }
    public string Field { get; }
    public decimal Supplied { get; }
    public decimal Cap { get; }

    public PlanLimitValidationException(
        string planId,
        string versionId,
        int planYear,
        string field,
        string message,
        decimal supplied,
        decimal cap) : base(message)
    {
        PlanId = planId ?? string.Empty;
        VersionId = versionId ?? string.Empty;
        PlanYear = planYear;
        Field = field;
        Supplied = supplied;
        Cap = cap;
    }
}
