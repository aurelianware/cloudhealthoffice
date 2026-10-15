using CloudHealthOffice.BenchmarkClaimGenerator.Generators;
using CloudHealthOffice.BenchmarkClaimGenerator.Models;

namespace CloudHealthOffice.Tools.MccPlatformValidator;

/// <summary>
/// The validation plan's default fee schedule, seeded through benefit-plan-service
/// <c>PUT /api/v1/plans/{planId}/default-fee-schedule</c>.
///
/// <para>
/// The claims pipeline prices every line through benefit-plan-service
/// <c>resolve-rates</c> (FeeScheduleEngine). MCC providers have no contract in the
/// engine's ProviderContracts store, so a line is priced only when the plan has a
/// default schedule with a matching line; otherwise the engine falls back to billed
/// charges and PricingStage pends the claim NOCONTRACT. The schedule is
/// <see cref="SyntheticFeeScheduleGenerator.GeneratePlanDefaultSchedule"/>: the
/// generator's existing CPT/HCPCS/CDT flat amounts plus a blank-procedure flat-rate
/// line for every revenue code the institutional generator emits. Flat rates only —
/// a percent-of-Medicare line has no Medicare reference for a revenue code and
/// would resolve Unresolved.
/// </para>
///
/// <para>
/// Idempotent per tenant + plan: the PUT replaces the plan's single default
/// schedule (server id <c>{tenant}:plan-default:{planId}</c>), and the payload is
/// deterministic, so a rerun writes the same document.
/// </para>
/// </summary>
public static class MccPlanDefaultFeeSchedule
{
    /// <summary>Effective from before any generated (or date-normalized) service date.</summary>
    public static readonly DateTime EffectiveDate = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>benefit-plan-service route for a plan's default fee schedule.</summary>
    public static string Path(Guid planId) => $"/api/v1/plans/{planId:D}/default-fee-schedule";

    /// <summary>The request body (serialized with the validator's camelCase web options).</summary>
    public static PlanDefaultFeeScheduleSeed BuildPayload()
    {
        var schedule = SyntheticFeeScheduleGenerator.GeneratePlanDefaultSchedule(EffectiveDate);
        return new PlanDefaultFeeScheduleSeed(
            schedule.Name,
            EffectiveDate,
            schedule.Lines
                .Select(l => new PlanDefaultFeeScheduleSeedLine(
                    l.ProcedureCode,
                    l.RevenueCode,
                    l.Modifier,
                    l.AllowedAmount,
                    l.MultipleProcedureIndicator))
                .ToList());
    }

    /// <summary>
    /// The line charge to submit: the line total, charge × units (X12 837 SV102 /
    /// SV203). The generator's <see cref="ClaimLine.ChargeAmount"/> is per unit (its
    /// claim total is Σ charge × units), and pricing treats the submitted charge as
    /// the line total, so a per-diem room-and-board line billed for N days must carry
    /// N days of charges or its allowed amount (daily rate × N) would exceed billed.
    /// Units are submitted as at least 1, so the same floor applies here.
    /// </summary>
    public static decimal LineChargeTotal(ClaimLine line)
        => line.ChargeAmount * (line.Units <= 0 ? 1 : line.Units);
}

/// <summary>Body of the plan default fee schedule PUT.</summary>
public sealed record PlanDefaultFeeScheduleSeed(
    string Name,
    DateTime EffectiveDate,
    IReadOnlyList<PlanDefaultFeeScheduleSeedLine> Lines);

/// <summary>One flat-rate line (dollars per unit). A revenue-code line has a blank procedure code.</summary>
public sealed record PlanDefaultFeeScheduleSeedLine(
    string ProcedureCode,
    string? RevenueCode,
    string? Modifier,
    decimal Rate,
    int? MultipleProcedureIndicator);
