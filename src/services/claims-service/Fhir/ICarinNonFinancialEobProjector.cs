using System.Text.Json.Nodes;
using ClaimsService.Models;

namespace ClaimsService.Fhir;

/// <summary>
/// Projects a CHO claim record to a CARIN Blue Button non-financial
/// <c>ExplanationOfBenefit</c> — the Provider Access payload. See
/// <see cref="CarinNonFinancialEobProjector"/> for what that profile family is
/// and why it is not the Patient Access EOB.
/// </summary>
public interface ICarinNonFinancialEobProjector
{
    JsonObject Project(Claim claim);
}
