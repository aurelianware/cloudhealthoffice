using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using EngineFeeScheduleType = CloudHealthOffice.FeeScheduleEngine.Domain.FeeScheduleType;

namespace BenefitPlanService.Services;

/// <summary>
/// A stay the fee schedule engine priced as one claim-level amount — a DRG case
/// rate or an all-inclusive per diem, allocated across the lines
/// (<see cref="PricingResult.IsPerStayRate"/>). The benefit engine must then take
/// its claim-level inpatient path: one inpatient copay, deductible and coinsurance
/// once, on the claim's total allowed. Shared by adjudication
/// (<c>AdjudicationController</c>) and prospective estimates
/// (<see cref="PaymentEstimateService"/>) so both cost-share a stay the same way.
/// </summary>
public sealed record PerStayPricing(
    InpatientPricingMethod Method, decimal ClaimAllowed, string? DrgCode, int? LengthOfStay)
{
    /// <summary>Null for a non-institutional claim or per-line pricing.</summary>
    public static PerStayPricing? Resolve(
        string claimTypeCode, string? drgCode, int? lengthOfStay, PricingResultSet pricingResults)
    {
        if (claimTypeCode != "837I")
            return null;

        var perStayLines = pricingResults.LineResults.Where(r => r.IsPerStayRate).ToList();
        if (perStayLines.Count == 0)
            return null;

        var method = perStayLines.Any(r => r.FeeScheduleType == EngineFeeScheduleType.Drg)
            ? InpatientPricingMethod.DrgCaseRate
            : InpatientPricingMethod.PerDiem;

        return new PerStayPricing(
            method,
            pricingResults.LineResults.Sum(r => r.AllowedAmount),
            string.IsNullOrWhiteSpace(drgCode) ? null : drgCode.Trim(),
            lengthOfStay);
    }
}
