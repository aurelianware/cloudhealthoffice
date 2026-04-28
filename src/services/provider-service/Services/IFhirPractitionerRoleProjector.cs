using System.Text.Json.Nodes;
using ProviderService.Models;

namespace ProviderService.Services;

/// <summary>
/// Projects a single <see cref="NetworkParticipation"/> on an Active
/// <see cref="ProviderType.Individual"/> <see cref="Provider"/> into a FHIR
/// R4 PractitionerRole resource (as a <see cref="JsonObject"/>). Mirrors
/// the design of <see cref="IFhirPractitionerProjector"/> (capability 5.7):
/// hand-built to avoid the Hl7.Fhir.R4 transitive dep graph and keep
/// serialization deterministic for tests.
///
/// <para>
/// Returns <c>null</c> in any of these cases — caller maps null to FHIR
/// <c>OperationOutcome</c> 404 (read path) or skips the row (search):
/// <list type="bullet">
///   <item><c>provider.ProviderType != Individual</c>. Organization-type
///   providers project as FHIR Organization (capability 5.9), not
///   PractitionerRole.</item>
///   <item><c>provider.VersionState != Active</c> or <c>provider.Status != Active</c>.
///   PractitionerRole is only projected from the head Active version.</item>
///   <item><c>participation.NetworkId is null</c>. Legacy participations
///   without a network reference are invisible to the FHIR surface (same
///   posture as the 5.4 roster API). Backfill is per-tenant operational
///   work tracked in <c>docs/architecture/network-participation-backfill.md</c>.</item>
///   <item>The composite-tuple id would exceed the FHIR R4 <c>id</c>
///   grammar's 64-character limit (e.g. an unusually long
///   <c>NetworkId</c> stretches the encoding past the cap). Emitting a
///   non-conformant id would silently break consumers; the row is
///   omitted instead.</item>
///   <item>Any required id component (NPI, NetworkId) is missing.</item>
/// </list>
/// </para>
///
/// <para>
/// The projection emits <c>meta.profile</c> with both the US Core 6.1.0
/// PractitionerRole profile and the Da Vinci Plan-Net 1.1.0
/// PractitionerRole profile. Required US Core elements (practitioner,
/// active) are honored. Plan-Net cardinality (organization, code,
/// specialty, telecom, period) is honored where CHO has data today;
/// extended Plan-Net extensions (cultural competency, accessibility,
/// populations served) remain deferred to capability 5.17.
/// </para>
///
/// <para>
/// Verification metadata stays on the linked Practitioner per Decision 5
/// of the 5.8 plan-phase. PractitionerRole carries panel-gating
/// information via a CHO-canonical extension (Decision 9), but no
/// integrity-score extension — consumers dereference
/// <c>PractitionerRole.practitioner</c> for that.
/// </para>
/// </summary>
public interface IFhirPractitionerRoleProjector
{
    /// <summary>
    /// Project a single network participation. <paramref name="network"/>
    /// is the resolved <see cref="Organization"/> head version when known
    /// — supplied so the projection can emit the Organization display
    /// name; null is acceptable and produces a reference-only
    /// <c>organization</c> field.
    /// </summary>
    JsonObject? Project(NetworkParticipation participation, Provider provider, Organization? network);

    /// <summary>
    /// Encode the canonical composite-tuple PractitionerRole id from the
    /// inputs that uniquely identify a participation
    /// (<c>NPI</c>, <c>LineOfBusiness</c>, <c>EffectiveDate</c>,
    /// <c>NetworkId</c>). Format: <c>{npi}-{lobInt}-{yyyymmdd}-{networkId}</c>.
    /// Returns null when any required component is missing or when the
    /// composite would exceed FHIR R4's 64-character <c>id</c> grammar
    /// limit. Search callers skip the row in that case; the read path
    /// surfaces the resulting non-addressable resource as a 404
    /// <c>OperationOutcome</c> (consistent with the existing
    /// null-handling shape).
    /// </summary>
    string? EncodeId(NetworkParticipation participation, Provider provider);

    /// <summary>
    /// Decode a PractitionerRole id back to its composite tuple. Returns
    /// null when the id does not match the canonical shape — caller maps
    /// to a 404 OperationOutcome.
    /// </summary>
    PractitionerRoleId? DecodeId(string id);
}

/// <summary>
/// Decoded composite-tuple PractitionerRole id (capability 5.8 Decision 6).
/// </summary>
/// <param name="Npi">10-digit National Provider Identifier of the linked
/// individual provider.</param>
/// <param name="LineOfBusiness">LOB enum value from the participation.</param>
/// <param name="EffectiveDate">UTC <c>DateTime.Date</c> of the
/// participation's <c>EffectiveDate</c>.</param>
/// <param name="NetworkId">Chain key of the linked
/// <see cref="Organization"/>.</param>
public sealed record PractitionerRoleId(
    string Npi,
    LineOfBusiness LineOfBusiness,
    DateTime EffectiveDate,
    string NetworkId);
