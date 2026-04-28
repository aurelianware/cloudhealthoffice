using System.Text.Json.Nodes;
using ProviderService.Models;

namespace ProviderService.Services;

/// <summary>
/// Projects a CHO <see cref="Organization"/> network entity OR a
/// <see cref="Provider"/> with <see cref="ProviderType.Organization"/>
/// into a FHIR R4 Organization resource (as a <see cref="JsonObject"/>).
/// Mirrors <see cref="IFhirPractitionerProjector"/> (5.7) and
/// <see cref="IFhirPractitionerRoleProjector"/> (5.8): hand-built to avoid
/// the Hl7.Fhir.R4 transitive dep graph and keep serialization
/// deterministic for tests.
///
/// <para>
/// Two source entities, one FHIR resource type (capability 5.9 Decision).
/// A payer-defined <see cref="Organization"/> network projects with
/// <c>type=ins</c>; a <see cref="Provider"/> with
/// <c>ProviderType=Organization</c> (a facility, clinic, or group
/// practice) projects with <c>type=prov</c>.
/// </para>
///
/// <para>
/// The <c>id</c> field encodes the source-entity key:
/// <list type="bullet">
///   <item>Provider-as-Org: <c>id = NPI</c> (10 digits, matches
///   <see cref="IFhirPractitionerProjector"/>'s treatment of NPI as the
///   FHIR id).</item>
///   <item>Network Organization: <c>id = OrganizationId</c> (chain key,
///   ULID / GUID-shaped by convention).</item>
/// </list>
/// </para>
///
/// <para>
/// <c>meta.profile</c> emits both the US Core 6.1.0 Organization profile
/// and the Da Vinci Plan-Net 1.1.0 Organization profile. Required US Core
/// elements (identifier, active, name, type) are honored. Extended Plan-Net
/// extensions (organizational accessibility, languages-of-service,
/// populations served) are deferred to capability 5.17.
/// </para>
///
/// <para>
/// Returns <c>null</c> in these cases — caller maps null to FHIR
/// <c>OperationOutcome</c> 404 (read path) or skips the row (search):
/// <list type="bullet">
///   <item><c>provider.ProviderType == Individual</c> — Individual
///   providers project as FHIR Practitioner (5.7), not Organization.</item>
///   <item>Provider <c>VersionState != Active</c> or <c>Status != Active</c>
///   — non-Active version rows are not directory-eligible.</item>
///   <item>Provider <c>OrganizationName</c> is null or empty — required for
///   FHIR Organization.name.</item>
///   <item>Network <c>VersionState != Active</c> — only the head Active
///   version is projected.</item>
///   <item>Network <c>Name</c> is null or empty — required for FHIR
///   Organization.name (US Core 6.1.0 requires name 1..1).</item>
///   <item>Network has no projectable <c>Identifiers</c> (all entries have
///   blank system or value) — US Core 6.1.0 requires identifier 1..*; a
///   network with no resolvable identifier cannot be emitted
///   conformantly.</item>
/// </list>
/// </para>
/// </summary>
public interface IFhirOrganizationProjector
{
    /// <summary>
    /// Project a payer-defined <see cref="Organization"/> network entity to a
    /// FHIR Organization with <c>type=ins</c>. Returns null when the
    /// network version is not Active, when <see cref="Organization.Name"/> is
    /// null or empty, or when the network has no projectable identifiers (US
    /// Core 6.1.0 requires identifier 1..*).
    /// </summary>
    JsonObject? Project(Organization network);

    /// <summary>
    /// Project a <see cref="Provider"/> with
    /// <see cref="ProviderType.Organization"/> to a FHIR Organization with
    /// <c>type=prov</c>. Returns null for Individual providers, non-Active
    /// versions, or providers without an OrganizationName.
    /// </summary>
    JsonObject? Project(Provider provider);
}
