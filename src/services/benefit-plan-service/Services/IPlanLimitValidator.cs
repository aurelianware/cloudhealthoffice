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
///
/// <para>
/// <b>Advisory warnings.</b> Ambiguous-but-legal configurations (Aggregate
/// plan with a $0 family deductible / OOP max and a positive individual
/// one) do not reject: they emit a structured warning and increment
/// <c>cho.benefit_plan.plan_limit_validation_warnings.total</c>, following
/// the <see cref="INetworkTierSoftValidator"/> soft-validation pattern.
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

        // COB deductible crediting: one of the three named settings
        // (NaicFullCredit, MemberPaidOnly, NoDeductible). A numeric value
        // outside the enum would silently project as NAIC full credit.
        if (!Enum.IsDefined(plan.CobDeductibleCredit))
        {
            ChoMetrics.PlanLimitValidationFailures.Add(
                1,
                new KeyValuePair<string, object?>("cho.caller", caller.ToString()),
                new KeyValuePair<string, object?>("cho.tenant_id", plan.TenantId ?? string.Empty),
                new KeyValuePair<string, object?>("cho.reason", "InvalidCobDeductibleCredit"));

            throw new PlanLimitValidationException(
                plan.PlanId,
                plan.VersionId,
                planYear,
                field: "cobDeductibleCredit",
                message: $"cobDeductibleCredit ({(int)plan.CobDeductibleCredit}) is not a supported value. " +
                         "Use NaicFullCredit (default), MemberPaidOnly or NoDeductible.",
                supplied: (int)plan.CobDeductibleCredit,
                cap: 0);
        }

        // An HDHP must apply its deductible before paying non-preventive
        // benefits (IRC §223(c)(2), the HSA-eligibility condition);
        // NoDeductible skips it whenever the plan pays after another payer.
        if (plan.PlanType == PlanType.HDHP && plan.CobDeductibleCredit == CobDeductibleCredit.NoDeductible)
        {
            ChoMetrics.PlanLimitValidationFailures.Add(
                1,
                new KeyValuePair<string, object?>("cho.caller", caller.ToString()),
                new KeyValuePair<string, object?>("cho.tenant_id", plan.TenantId ?? string.Empty),
                new KeyValuePair<string, object?>("cho.reason", "HdhpNoDeductibleCob"));

            throw new PlanLimitValidationException(
                plan.PlanId,
                plan.VersionId,
                planYear,
                field: "cobDeductibleCredit",
                message: "cobDeductibleCredit NoDeductible is not allowed on an HDHP plan: an HDHP must apply its " +
                         "deductible (IRC §223(c)(2)). Use NaicFullCredit (default) or MemberPaidOnly.",
                supplied: (int)plan.CobDeductibleCredit,
                cap: 0);
        }

        WarnOnAmbiguousAggregateLimits(plan, cs, caller);

        _logger.LogDebug(
            "PlanLimitValidator passed for plan {PlanId} version {VersionId} caller={Caller} planYear={PlanYear}",
            SanitizeForLog(plan.PlanId),
            SanitizeForLog(plan.VersionId),
            caller,
            planYear);
    }

    /// <summary>
    /// Advisory (soft) check — structured warning + counter, write still
    /// succeeds. The in-network family limits are non-nullable, so an
    /// Aggregate plan with a $0 family deductible / OOP max and a positive
    /// individual one is ambiguous: the engine projection reads 0 as
    /// "not set" and applies the individual limit as each member's pool.
    /// A true $0 family deductible cannot currently be expressed.
    /// </summary>
    private void WarnOnAmbiguousAggregateLimits(BenefitPlan plan, CostSharing cs, PlanLimitWriteCaller caller)
    {
        if (plan.FamilyAccumulatorModel != FamilyAccumulatorModel.Aggregate) return;

        // Same individual-limit resolution as ChoBenefitPlanProvider.
        var individualDeductible = cs.IndividualDeductible > 0 ? cs.IndividualDeductible : cs.InNetworkDeductible;
        var individualOop = cs.IndividualOutOfPocketMax > 0 ? cs.IndividualOutOfPocketMax : cs.InNetworkOutOfPocketMax;

        if (cs.FamilyDeductible <= 0 && individualDeductible > 0)
            EmitWarning(plan, caller, "AggregateZeroFamilyDeductible",
                "costSharing.familyDeductible", "deductible", individualDeductible);

        if (cs.FamilyOutOfPocketMax <= 0 && individualOop > 0)
            EmitWarning(plan, caller, "AggregateZeroFamilyOop",
                "costSharing.familyOutOfPocketMax", "out-of-pocket maximum", individualOop);
    }

    private void EmitWarning(
        BenefitPlan plan, PlanLimitWriteCaller caller, string reason,
        string field, string limitName, decimal individualLimit)
    {
        ChoMetrics.PlanLimitValidationWarnings.Add(
            1,
            new KeyValuePair<string, object?>("cho.caller", caller.ToString()),
            new KeyValuePair<string, object?>("cho.tenant_id", plan.TenantId ?? string.Empty),
            new KeyValuePair<string, object?>("cho.reason", reason));

        _logger.LogWarning(
            "PlanLimitWarning {Reason} on plan write. caller={Caller} tenantId={TenantId} planId={PlanId} versionId={VersionId} field={Field}: {Detail}",
            reason,
            caller,
            SanitizeForLog(plan.TenantId),
            SanitizeForLog(plan.PlanId),
            SanitizeForLog(plan.VersionId),
            field,
            AmbiguousAggregateLimitMessage(limitName, individualLimit));
    }

    internal static string AmbiguousAggregateLimitMessage(string limitName, decimal individualLimit)
        => $"Aggregate plan has a $0 family {limitName}. $0 is treated as \"not set\": the individual " +
           $"{limitName} ({individualLimit:C0}) will be applied per member. A true $0 family {limitName} " +
           "cannot currently be expressed.";

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
