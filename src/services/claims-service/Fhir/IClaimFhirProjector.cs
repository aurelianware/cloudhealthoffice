using System.Text.Json.Nodes;
using ClaimsService.Models;

namespace ClaimsService.Fhir;

/// <summary>
/// Projects a CHO claim record to a FHIR R4 Claim — the claim <em>as billed</em>.
/// The adjudicated view of the same record is
/// <see cref="IExplanationOfBenefitProjector"/>; the two are complementary and
/// share the chain-stable resource id.
/// </summary>
public interface IClaimFhirProjector
{
    JsonObject Project(Claim claim);
}
