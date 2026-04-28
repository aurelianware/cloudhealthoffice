using System.Text.Json.Nodes;
using ProviderService.Models;

namespace ProviderService.Services;

/// <summary>
/// Projects a <see cref="Provider"/> with <see cref="ProviderType.Individual"/>
/// into a FHIR R4 Practitioner resource (as a <see cref="JsonObject"/>).
/// Mirrors <c>IFhirPatientProjector</c> in member-service: hand-built to
/// avoid the Hl7.Fhir.R4 transitive dep graph and keep serialization
/// deterministic for tests.
///
/// <para>
/// Returns <c>null</c> when called with <see cref="ProviderType.Organization"/>
/// — those project as FHIR Organization (capability 5.8), not Practitioner.
/// Caller maps null to FHIR <c>OperationOutcome</c> 404.
/// </para>
///
/// <para>
/// The projection emits <c>meta.profile</c> with both the US Core 6.1.0
/// Practitioner profile and the Da Vinci Plan-Net 1.1.0 Practitioner
/// profile. Required US Core elements are honored (identifier, active,
/// name, address, telecom, qualification). Plan-Net <c>qualification.code</c>
/// is emitted with full NUCC coding for the primary specialty and as
/// text-only CodeableConcept entries for secondary specialties (no
/// parallel taxonomy-code list exists on Provider today). Extended
/// Plan-Net extensions (cultural competency, accessibility, populations
/// served) are deferred to capability 5.17.
/// </para>
///
/// <para>
/// Practitioner.gender is intentionally NOT emitted: there is no Gender
/// field on <see cref="Provider"/> today. US Core 6.1.0 cardinality is
/// Must Support 0..1, so omission is conformant. Capability 5.17 adds
/// the field alongside other Plan-Net demographics.
/// </para>
/// </summary>
public interface IFhirPractitionerProjector
{
    /// <summary>
    /// Project a provider without integrity context. Equivalent to
    /// <see cref="Project(Provider, ProviderIntegrityProjection?)"/> with
    /// <c>integrity = null</c>.
    /// </summary>
    JsonObject? Project(Provider provider);

    /// <summary>
    /// Project a provider; when <paramref name="integrity"/> is provided,
    /// emit a CHO-prefixed integrity-score extension so consumers can
    /// surface the Provider Integrity verification result (capability
    /// 5.4.5) without an extra lookup.
    /// </summary>
    JsonObject? Project(Provider provider, ProviderIntegrityProjection? integrity);
}
