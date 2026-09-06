using Hl7.Fhir.Model;

namespace FhirService.Services.Clinical;

/// <summary>
/// The USCDI clinical resource types Cloud Health Office serves through Patient
/// and Provider Access (CMS-0057-F PAT-02), and everything the rest of the
/// service needs to know about each one.
///
/// THIS TYPE IS THE SINGLE SOURCE OF TRUTH. The SMART scope layer, the Provider
/// Access authorization filter, the Payer-to-Payer import classification, the
/// CapabilityStatement, and the routes all read their clinical inventory from
/// here, and structural tests pin them to it. A resource type therefore cannot
/// become reachable through one of those layers while being forgotten by
/// another — which is exactly how a new resource would end up served without a
/// consent check.
///
/// WHERE THE INVENTORY COMES FROM. It is not a wish list and not a guess. It is
/// the USCDI clinical data classes this repository already documents as
/// CMS-0057-F obligations (docs/features/CMS-0057-F-COMPLIANCE.md, "USCDI Data
/// Classes"), mapped onto their US Core R4 resource types, MINUS the classes CHO
/// already serves elsewhere:
///
///   Patient Demographics  -> Patient              (PatientController, PAT-01)
///   Health Insurance Info -> Coverage             (CoverageController)
///   Coverage / claims     -> ExplanationOfBenefit (ExplanationOfBenefitController)
///   Clinical Notes        -> DocumentReference    (DocumentReferenceController)
///   Provenance            -> resource metadata    (meta.source, below)
///
/// What was left is what PAT-02 was PARTIAL for: the clinical classes that had
/// nowhere in CHO to live, named as such by the Payer-to-Payer import policy
/// ("Condition, Observation, Procedure, MedicationRequest") and by the P2P-02
/// acceptance rationale ("the rest of the USCDI clinical set — the same gap that
/// keeps PAT-02 PARTIAL"). Those are the twelve types below.
///
/// ADDING A TYPE. Add an entry here, and every layer follows. But add it only
/// once the read path is real: an entry in this table is a claim that
/// <c>GET {Type}/{id}</c> and <c>GET {Type}?patient=</c> genuinely serve stored
/// data, and <see cref="ClinicalResourceInventory"/> is what the
/// CapabilityStatement is generated from.
/// </summary>
public static class ClinicalResourceInventory
{
    // ── Search parameter names ────────────────────────────────────────────────
    // Spelled once, so a controller, the CapabilityStatement and a test cannot
    // disagree about what a parameter is called.

    public const string IdParam = "_id";
    public const string PatientParam = "patient";
    public const string SubjectParam = "subject";

    /// <summary>
    /// One clinical resource type: how CHO binds it to a member, which searches
    /// it answers, and which USCDI data class it discharges.
    /// </summary>
    /// <param name="ResourceType">FHIR R4 resource type name.</param>
    /// <param name="SubjectElement">
    /// The element naming the member — <c>subject</c> for most types,
    /// <c>patient</c> for AllergyIntolerance, Device and Immunization. This is
    /// the element CHO REWRITES to the trusted member binding when serving, so a
    /// prior payer's subject reference is never what a reader resolves.
    /// </param>
    /// <param name="SupportsSubjectSearch">
    /// Whether FHIR R4 defines a <c>subject</c> search parameter for the type.
    /// AllergyIntolerance, Device and Immunization define only <c>patient</c>, so
    /// CHO advertises only <c>patient</c> for them rather than inventing one.
    /// </param>
    /// <param name="UscdiDataClasses">The USCDI data class(es) the type carries.</param>
    /// <param name="BindSubject">Sets the subject/patient element to CHO's member reference.</param>
    /// <param name="ReadSubject">Reads the subject/patient reference, for assertions and tests.</param>
    public sealed record Entry(
        string ResourceType,
        string SubjectElement,
        bool SupportsSubjectSearch,
        IReadOnlyList<string> UscdiDataClasses,
        Action<Resource, ResourceReference> BindSubject,
        Func<Resource, string?> ReadSubject)
    {
        /// <summary>
        /// Exactly the search parameters CHO implements for this type — nothing
        /// aspirational. <c>_id</c> and <c>patient</c> always; <c>subject</c>
        /// where R4 defines it.
        /// </summary>
        public IReadOnlyList<string> SearchParameters => SupportsSubjectSearch
            ? [IdParam, PatientParam, SubjectParam]
            : [IdParam, PatientParam];

        /// <summary>The search parameters that name the member for this type.</summary>
        public IReadOnlyList<string> MemberSearchParameters => SupportsSubjectSearch
            ? [PatientParam, SubjectParam]
            : [PatientParam];
    }

    private static readonly Entry[] Inventory =
    [
        new("AllergyIntolerance", "patient", SupportsSubjectSearch: false,
            ["Allergies and Intolerances"],
            (r, s) => ((AllergyIntolerance)r).Patient = s,
            r => ((AllergyIntolerance)r).Patient?.Reference),

        new("CarePlan", "subject", SupportsSubjectSearch: true,
            ["Assessment and Plan of Treatment"],
            (r, s) => ((CarePlan)r).Subject = s,
            r => ((CarePlan)r).Subject?.Reference),

        new("CareTeam", "subject", SupportsSubjectSearch: true,
            ["Care Team Members"],
            (r, s) => ((CareTeam)r).Subject = s,
            r => ((CareTeam)r).Subject?.Reference),

        new("Condition", "subject", SupportsSubjectSearch: true,
            ["Problems", "Health Concerns"],
            (r, s) => ((Condition)r).Subject = s,
            r => ((Condition)r).Subject?.Reference),

        new("Device", "patient", SupportsSubjectSearch: false,
            ["Unique Device Identifiers"],
            (r, s) => ((Device)r).Patient = s,
            r => ((Device)r).Patient?.Reference),

        new("DiagnosticReport", "subject", SupportsSubjectSearch: true,
            ["Laboratory", "Diagnostic Imaging"],
            (r, s) => ((DiagnosticReport)r).Subject = s,
            r => ((DiagnosticReport)r).Subject?.Reference),

        new("Goal", "subject", SupportsSubjectSearch: true,
            ["Goals"],
            (r, s) => ((Goal)r).Subject = s,
            r => ((Goal)r).Subject?.Reference),

        new("Immunization", "patient", SupportsSubjectSearch: false,
            ["Immunizations"],
            (r, s) => ((Immunization)r).Patient = s,
            r => ((Immunization)r).Patient?.Reference),

        new("MedicationDispense", "subject", SupportsSubjectSearch: true,
            ["Medications"],
            (r, s) => ((MedicationDispense)r).Subject = s,
            r => ((MedicationDispense)r).Subject?.Reference),

        new("MedicationRequest", "subject", SupportsSubjectSearch: true,
            ["Medications"],
            (r, s) => ((MedicationRequest)r).Subject = s,
            r => ((MedicationRequest)r).Subject?.Reference),

        new("Observation", "subject", SupportsSubjectSearch: true,
            ["Laboratory", "Vital Signs", "Smoking Status", "Clinical Tests"],
            (r, s) => ((Observation)r).Subject = s,
            r => ((Observation)r).Subject?.Reference),

        new("Procedure", "subject", SupportsSubjectSearch: true,
            ["Procedures"],
            (r, s) => ((Procedure)r).Subject = s,
            r => ((Procedure)r).Subject?.Reference),
    ];

    private static readonly IReadOnlyDictionary<string, Entry> ByType =
        Inventory.ToDictionary(e => e.ResourceType, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every clinical resource type CHO serves, sorted for determinism.</summary>
    public static IReadOnlyList<Entry> All { get; } =
        [.. Inventory.OrderBy(e => e.ResourceType, StringComparer.Ordinal)];

    /// <summary>The type names alone — what the SMART, consent and import layers key on.</summary>
    public static IReadOnlyList<string> ResourceTypes { get; } =
        [.. All.Select(e => e.ResourceType)];

    /// <summary>
    /// Case-insensitive membership set. ASP.NET route matching is
    /// case-insensitive, so an ordinal set here would let a caller reach a
    /// controller through <c>/fhir/r4/observation/…</c> that the authorization
    /// layers did not recognise as clinical.
    /// </summary>
    public static IReadOnlySet<string> ResourceTypeSet { get; } =
        new HashSet<string>(ResourceTypes, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every member-naming search parameter across the whole inventory. The
    /// Provider Access filter needs this: a search it cannot resolve a member
    /// from is refused, so a parameter missing here becomes a denial, not a leak.
    /// </summary>
    public static IReadOnlyList<string> MemberSearchParameters { get; } =
        [.. All.SelectMany(e => e.MemberSearchParameters).Distinct(StringComparer.Ordinal)
               .OrderBy(p => p, StringComparer.Ordinal)];

    /// <summary>The USCDI data classes this inventory discharges, sorted and de-duplicated.</summary>
    public static IReadOnlyList<string> UscdiDataClasses { get; } =
        [.. All.SelectMany(e => e.UscdiDataClasses).Distinct(StringComparer.Ordinal)
               .OrderBy(c => c, StringComparer.Ordinal)];

    /// <summary>True when CHO serves this resource type as clinical data.</summary>
    public static bool IsClinical(string? resourceType)
        => !string.IsNullOrEmpty(resourceType) && ResourceTypeSet.Contains(resourceType);

    /// <summary>The entry for a type, or null when CHO does not serve it.</summary>
    public static Entry? Find(string? resourceType)
        => resourceType is not null && ByType.TryGetValue(resourceType, out var entry) ? entry : null;

    /// <summary>
    /// The canonical spelling of a type name the caller may have cased
    /// differently, so scope strings and store queries are built from CHO's
    /// spelling rather than the request's.
    /// </summary>
    public static string? Canonicalize(string? resourceType)
        => Find(resourceType)?.ResourceType;

    /// <summary>
    /// Route constraint alternation for the clinical controller — the exact type
    /// names, so no other resource can be routed there. Built from the inventory
    /// rather than typed out, so the routes cannot drift from the table.
    /// </summary>
    public static string RouteAlternation { get; } = string.Join('|', ResourceTypes);
}
