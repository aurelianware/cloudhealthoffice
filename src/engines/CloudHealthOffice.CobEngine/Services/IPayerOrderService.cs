using CloudHealthOffice.CobEngine.Domain;

namespace CloudHealthOffice.CobEngine.Services;

/// <summary>
/// Determines payer sequence (primary / secondary / tertiary) for a coverage
/// using standard coordination-of-benefits order rules.
/// </summary>
public interface IPayerOrderService
{
    /// <summary>
    /// Determine payer sequence for <paramref name="thisCoverage"/> given a list of
    /// all coverages the member holds. The list should include <paramref name="thisCoverage"/>.
    /// </summary>
    PayerOrderResult DetermineOrder(InsuredInfo thisCoverage, IReadOnlyList<InsuredInfo> allCoverages);
}
