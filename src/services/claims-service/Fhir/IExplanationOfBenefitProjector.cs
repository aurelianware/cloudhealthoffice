using System.Text.Json.Nodes;
using ClaimsService.Models;

namespace ClaimsService.Fhir;

/// <summary>
/// Projects a <see cref="Claim"/> into a FHIR R4 ExplanationOfBenefit resource
/// (as a <see cref="JsonObject"/>). Hand-built to avoid the Hl7.Fhir.R4
/// transitive dep graph — same pattern as
/// <c>MemberService.Services.IFhirPatientProjector</c>.
/// </summary>
public interface IExplanationOfBenefitProjector
{
    JsonObject Project(Claim claim);
}
