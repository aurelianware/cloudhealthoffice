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
}
