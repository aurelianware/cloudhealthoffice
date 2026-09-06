namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// The Da Vinci PAS wire vocabulary: operation canonicals, profile URLs,
/// extension URLs and the X12 code systems PAS binds to.
///
/// Every value here was read out of the published PAS IG package rather than
/// copied from an implementation, because the point of this harness is to catch
/// an implementation that names something differently. Where CHO and the pinned
/// external implementation disagree, the IG is what settles it — so the IG's
/// spelling is the one the harness holds, and a scenario compares both sides
/// against it rather than against each other.
///
/// The two halves of the inquiry operation deliberately do not match: the
/// OperationDefinition is <c>Claim-inquiry</c>, its operation <c>code</c> is
/// <c>inquire</c>, and therefore the route is <c>Claim/$inquire</c>. Deriving
/// either from the other yields <c>Claim-inquire</c>, a canonical no published
/// PAS version defines. See docs/interop/davinci.md.
/// </summary>
public static class PasProtocol
{
    private const string Base = "http://hl7.org/fhir/us/davinci-pas/";
    private const string StructureDefinition = Base + "StructureDefinition/";

    // ── Operations ───────────────────────────────────────────────────────────

    /// <summary>PAS <c>Claim/$submit</c>: OperationDefinition id <c>Claim-submit</c>, code <c>submit</c>.</summary>
    public const string SubmitOperationCanonical = Base + "OperationDefinition/Claim-submit";

    /// <summary>
    /// PAS <c>Claim/$inquire</c>: OperationDefinition id <c>Claim-inquiry</c>,
    /// code <c>inquire</c>. Published under this canonical by PAS 1.0.0, 1.1.0,
    /// 2.0.1, 2.1.0, 2.2.0 and 2.2.1 — every release to date.
    /// </summary>
    public const string InquiryOperationCanonical = Base + "OperationDefinition/Claim-inquiry";

    /// <summary>
    /// The canonical CHO advertised for <c>$inquire</c> before this was resolved
    /// against the published IG, and which no PAS release defines. Kept as a
    /// named constant so a scenario can assert its absence by name rather than
    /// re-typing a string that looks plausible.
    /// </summary>
    public const string UnpublishedInquiryCanonical = Base + "OperationDefinition/Claim-inquire";

    /// <summary>The operation code in the URL — <c>Claim/$submit</c>.</summary>
    public const string SubmitOperationCode = "submit";

    /// <summary>The operation code in the URL — <c>Claim/$inquire</c>. Not "inquiry".</summary>
    public const string InquiryOperationCode = "inquire";

    /// <summary>The single input parameter both PAS operations take: a Bundle.</summary>
    public const string ResourceParameter = "resource";

    /// <summary>The output parameter <c>$inquire</c> repeats, once per matching authorization.</summary>
    public const string ResponseBundleParameter = "responseBundle";

    // ── Profiles ─────────────────────────────────────────────────────────────

    public const string RequestBundleProfile = StructureDefinition + "profile-pas-request-bundle";
    public const string InquiryRequestBundleProfile = StructureDefinition + "profile-pas-inquiry-request-bundle";
    public const string ResponseBundleProfile = StructureDefinition + "profile-pas-response-bundle";
    public const string InquiryResponseBundleProfile = StructureDefinition + "profile-pas-inquiry-response-bundle";
    public const string ClaimProfile = StructureDefinition + "profile-claim";
    public const string ClaimInquiryProfile = StructureDefinition + "profile-claim-inquiry";
    public const string ClaimResponseProfile = StructureDefinition + "profile-claimresponse";

    // ── Extensions ───────────────────────────────────────────────────────────

    /// <summary>Carries the payer's decision, on <c>ClaimResponse.item.adjudication</c>.</summary>
    public const string ReviewActionExtension = StructureDefinition + "extension-reviewAction";

    /// <summary>The X12 306 decision code, a sub-extension of reviewAction.</summary>
    public const string ReviewActionCodeExtension = StructureDefinition + "extension-reviewActionCode";

    /// <summary>
    /// "Item Level Review Number" — the sub-extension of reviewAction carrying the
    /// authorization number the payer issued. Its url is the bare token
    /// <c>number</c>, not an absolute URL: PAS fixes it that way because it is a
    /// complex extension's slice, and a reader that expects a full URL here finds
    /// nothing.
    /// </summary>
    public const string ReviewActionNumberSubExtension = "number";

    /// <summary>
    /// The authorization number as it travels on a Claim or ClaimResponse
    /// <c>item</c>. Note the asymmetry with the sub-extension above: the payer
    /// ISSUES the number inside reviewAction on the response, and a later inquiry
    /// QUOTES it back under this extension on the request.
    /// </summary>
    public const string AuthorizationNumberExtension = StructureDefinition + "extension-authorizationNumber";

    /// <summary>The pended-request tracking handle, on <c>item</c>. Issued where no authorization number is.</summary>
    public const string AdministrationReferenceNumberExtension =
        StructureDefinition + "extension-administrationReferenceNumber";

    /// <summary>Provider-assigned trace number echoed by the payer, on <c>item</c>.</summary>
    public const string ItemTraceNumberExtension = StructureDefinition + "extension-itemTraceNumber";

    public const string ItemPreAuthPeriodExtension = StructureDefinition + "extension-itemPreAuthPeriod";

    public const string CertificationTypeExtension = StructureDefinition + "extension-certificationType";

    // ── Terminology ──────────────────────────────────────────────────────────

    /// <summary>X12 005010/306 — Healthcare Services Decision Reason (the review action codes).</summary>
    public const string X12ReviewActionSystem = "https://codesystem.x12.org/005010/306";

    /// <summary>X12 005010/1365 — Service Type, bound to <c>Claim.item.category</c>.</summary>
    public const string X12ServiceTypeSystem = "https://codesystem.x12.org/005010/1365";

    /// <summary>X12 005010/1322 — Certification Type.</summary>
    public const string X12CertificationTypeSystem = "https://codesystem.x12.org/005010/1322";
}
