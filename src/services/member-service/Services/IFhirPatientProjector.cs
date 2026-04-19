using System.Text.Json.Nodes;
using MemberService.Controllers;
using MemberService.Models;

namespace MemberService.Services;

/// <summary>
/// Projects a <see cref="Member"/> into a FHIR R4 Patient resource (as a
/// <see cref="JsonObject"/>). Hand-built to avoid the Hl7.Fhir.R4 transitive
/// dep graph and keep serialization deterministic for tests.
/// </summary>
public interface IFhirPatientProjector
{
    /// <summary>
    /// Project a member without PCP context. Equivalent to <see cref="Project(Member, MemberPcpResponse?)"/>
    /// with <c>pcp = null</c> — kept for callers that don't have PCP data on hand.
    /// </summary>
    JsonObject Project(Member member);

    /// <summary>
    /// Project a member; when <paramref name="pcp"/> is provided, emit
    /// <c>Patient.generalPractitioner</c>[] with an NPI identifier so consumers
    /// can navigate to the Practitioner without an extra lookup.
    /// </summary>
    JsonObject Project(Member member, MemberPcpResponse? pcp);
}
