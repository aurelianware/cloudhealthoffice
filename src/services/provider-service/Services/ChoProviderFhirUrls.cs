namespace ProviderService.Services;

/// <summary>
/// Canonical URLs for FHIR artifacts the provider-service projector
/// emits. Mirrors the convention in fhir-service's
/// <c>ChoFhirCanonicalUrls</c> (base <c>http://fhir.cloudhealthoffice.com/</c>);
/// the integrity-score extension URL must be byte-identical between the
/// two services so consumers see one canonical extension regardless of
/// which service rendered the resource.
///
/// <para>
/// TODO: consolidate with fhir-service ChoFhirCanonicalUrls when a
/// shared FHIR-infrastructure project lands (capability 5.10 closer or
/// a Phase 2 cleanup PR). Provider-service does not reference
/// fhir-service today, so the constants are mirrored here.
/// </para>
/// </summary>
internal static class ChoProviderFhirUrls
{
    public const string Base                    = "http://fhir.cloudhealthoffice.com/";
    public const string StructureDefinitionBase = Base + "StructureDefinition/";

    /// <summary>
    /// CHO-prefixed extension carrying the cached Provider Integrity
    /// projection (capability 5.4.5 — IntegrityScore + IntegrityRating +
    /// LastVerifiedAt). Emitted on Practitioner resources only when
    /// <see cref="Models.Provider.IntegrityScore"/> is non-null.
    /// </summary>
    public const string ProviderIntegrityScoreExt =
        StructureDefinitionBase + "provider-integrity-score";

    // ── Standard FHIR / IG profile URLs ─────────────────────────────────

    public const string NpiSystem            = "http://hl7.org/fhir/sid/us-npi";
    public const string NuccTaxonomySystem   = "http://nucc.org/provider-taxonomy";
    public const string Bcp47LanguageSystem  = "urn:ietf:bcp:47";
    public const string Hl70360CodeSystem    = "http://terminology.hl7.org/CodeSystem/v2-0360";

    public const string UsCorePractitionerProfile =
        "http://hl7.org/fhir/us/core/StructureDefinition/us-core-practitioner";

    public const string PlanNetPractitionerProfile =
        "http://hl7.org/fhir/us/davinci-pdex-plan-net/StructureDefinition/plannet-Practitioner";

    public const string UsCorePractitionerRoleProfile =
        "http://hl7.org/fhir/us/core/StructureDefinition/us-core-practitionerrole";

    public const string PlanNetPractitionerRoleProfile =
        "http://hl7.org/fhir/us/davinci-pdex-plan-net/StructureDefinition/plannet-PractitionerRole";

    /// <summary>
    /// CHO grouped extension carrying panel-gating fields on a
    /// PractitionerRole resource (capability 5.8 Decision 9 — single
    /// top-level extension with sub-extensions, mirroring 5.7's
    /// <see cref="ProviderIntegrityScoreExt"/> shape). The five
    /// sub-extensions emit only when their source field is non-null;
    /// the parent extension is omitted entirely when all five are null.
    /// </summary>
    public const string PractitionerRolePanelGatingExt =
        StructureDefinitionBase + "practitionerrole-panel-gating";

    /// <summary>
    /// Coding system used for the LOBs surfaced inside the
    /// <c>accepted-lobs</c> sub-extension. CHO does not yet bind LOB to
    /// an external canonical value set; we publish under a CHO base so
    /// consumers see a stable system + code pair.
    /// </summary>
    public const string LineOfBusinessSystem = Base + "CodeSystem/line-of-business";

    // ── Organization profiles + terminology (capability 5.9) ────────────

    public const string UsCoreOrganizationProfile =
        "http://hl7.org/fhir/us/core/StructureDefinition/us-core-organization";

    public const string PlanNetOrganizationProfile =
        "http://hl7.org/fhir/us/davinci-pdex-plan-net/StructureDefinition/plannet-Organization";

    /// <summary>
    /// FHIR R4 Organization type value set code system. Used for the
    /// <c>type</c> coding that discriminates between a payer network
    /// (<c>ins</c>) and a provider-organization (<c>prov</c>).
    /// </summary>
    public const string OrganizationTypeCodeSystem =
        "http://terminology.hl7.org/CodeSystem/organization-type";

    /// <summary>
    /// HL7-canonical OID system for US Employer Identification Numbers
    /// (EIN / Tax ID). Plan-Net IG 1.1.0 § 2.3 expects this system when
    /// emitting a TaxId identifier on an Organization.
    /// </summary>
    public const string EinSystem = "urn:oid:2.16.840.1.113883.4.4";
}
