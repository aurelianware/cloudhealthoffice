using System.Text.Json.Nodes;
using MemberService.Models;

namespace MemberService.Services;

/// <summary>
/// Projects a <see cref="Member"/> into a FHIR R4 Patient resource (as a
/// <see cref="JsonObject"/>). Hand-built to avoid the Hl7.Fhir.R4 transitive
/// dep graph and keep serialization deterministic for tests.
/// </summary>
public interface IFhirPatientProjector
{
    JsonObject Project(Member member);
}
