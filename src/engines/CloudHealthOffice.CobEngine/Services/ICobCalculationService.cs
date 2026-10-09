using CloudHealthOffice.CobEngine.Domain;

namespace CloudHealthOffice.CobEngine.Services;

/// <summary>
/// Applies the COB calculation model (complementary or non-duplication) to
/// adjust secondary plan payment and member responsibility after the primary
/// payer has adjudicated.
/// </summary>
public interface ICobCalculationService
{
    /// <summary>
    /// Calculate COB-adjusted amounts for a single claim line.
    /// </summary>
    CobLineResult Calculate(CobLineInput input);

    /// <summary>
    /// Calculate COB-adjusted amounts for all lines in a claim.
    /// </summary>
    IReadOnlyList<CobLineResult> CalculateAll(IEnumerable<CobLineInput> lines);

    /// <summary>
    /// Claim-level COB (NAIC MDL-120 §7: the limits apply "for that claim"):
    /// this plan's payment and the member's share for the whole claim, then
    /// distributed to the units.
    /// </summary>
    CobClaimResult CalculateClaim(CobClaimInput input);
}
