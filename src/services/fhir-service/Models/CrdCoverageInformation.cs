using System.Text.Json;
using System.Text.Json.Serialization;

namespace FhirService.Models;

/// <summary>
/// What CHO determined for one ordered service. The card wording is derived from
/// this, and so is the <c>coverage-information</c> extension — so an EHR that
/// reads cards and an EHR that reads system actions cannot be told two different
/// things.
/// </summary>
public enum CrdDetermination
{
    /// <summary>Prior authorization is required.</summary>
    AuthorizationRequired,

    /// <summary>Additional clinical documentation is required for the order.</summary>
    DocumentationRequired,

    /// <summary>
    /// Prior authorization is explicitly not required — issued only from an
    /// administrator's positive classification, never inferred.
    /// </summary>
    NoAuthorizationRequired,

    /// <summary>
    /// CHO could not determine the requirement. Never equivalent to "not
    /// required"; the CRD IG has a code for exactly this (<c>indeterminate</c>),
    /// and requires a reason alongside it.
    /// </summary>
    Indeterminate,

    /// <summary>
    /// The service is not covered because CHO holds no coverage it could be
    /// covered under: the patient is not a member, the member's coverage has
    /// ended, or the Coverage the order names is not one CHO holds. CRD 2.2.1
    /// answers these with <c>not-covered</c> and the matching reason
    /// (no-member-found, no-active-coverage, coverage-not-found). It is a
    /// statement about eligibility, never about the service's rules, and it
    /// carries no <c>pa-needed</c> (crd-ci-q2).
    /// </summary>
    NotCovered,

    /// <summary>
    /// Prior authorization would be required, and CHO holds an approved one for
    /// this member that covers the service on its date: <c>pa-needed</c>
    /// <c>satisfied</c>, with the authorization's number as
    /// <c>satisfied-pa-id</c> (crd-ci-q5 requires the two together).
    /// </summary>
    AuthorizationSatisfied,
}

/// <summary>
/// Why CHO could not determine a requirement, for the <c>reason</c> sub-extension.
///
/// <para>
/// Only <see cref="Technical"/> maps onto a code in the IG's Coverage Assertion
/// Reasons value set. The others carry text alone: a CodeableConcept with text
/// and no coding is valid FHIR, and invariant <c>crd-ci-q7</c> is satisfied by
/// the text, so inventing a coding CHO cannot justify would add nothing but a
/// wrong claim.
/// </para>
/// </summary>
public enum CrdIndeterminateReason
{
    /// <summary>Nothing classifies the code: no tenant classification, no deployment configuration.</summary>
    Unclassified,

    /// <summary>The rule engine was unavailable, errored or timed out. IG code <c>technical</c>.</summary>
    Technical,

    /// <summary>The draft order carried no code to classify.</summary>
    NoCodedService,

    /// <summary>
    /// CHO classified the service as needing documentation but made no statement
    /// about prior authorization — which is not the same as saying none is needed.
    /// </summary>
    DocumentationOnlyClassification,

    /// <summary>
    /// The order is planned for after the member's coverage ends, so its
    /// coverage depends on a contract that is not yet in place (info-needed
    /// <c>contract-window</c>).
    /// </summary>
    OutsideCoveragePeriod,

    /// <summary>No member matched the hook's patient. IG code <c>no-member-found</c>.</summary>
    MemberNotFound,

    /// <summary>The member has no coverage in effect. IG code <c>no-active-coverage</c>.</summary>
    NoActiveCoverage,

    /// <summary>
    /// The member and coverage exist but do not carry what the coverage rules
    /// are keyed on, so no rule set resolves. IG code <c>coverage-not-found</c>.
    /// </summary>
    CoverageContextIncomplete,

    /// <summary>
    /// The member is known, but the Coverage the EHR sent (by its plan or group)
    /// is not one CHO holds for them. IG code <c>coverage-not-found</c>; used
    /// with <see cref="CrdDetermination.NotCovered"/>.
    /// </summary>
    CoverageNotFound,
}

/// <summary>
/// The Da Vinci CRD <c>coverage-information</c> extension, as CHO emits it on the
/// draft order carried back in a CDS Hooks system action.
///
/// <para>
/// This is where a CRD server states its actual determination in a form another
/// system can act on. Cards are prose: an EHR can display them and a human can
/// read them, but nothing downstream can branch on them. Until now CHO answered
/// with cards only, so every CRD consumer had to parse English to learn whether
/// prior authorization was required (gap-matrix G-44).
/// </para>
///
/// <para>
/// Built against CRD <b>2.2.1</b>, which is the family CHO declares. Two details
/// of that version matter here and are not in earlier ones: <c>indeterminate</c>
/// is a real code on <c>covered</c>, <c>pa-needed</c> and <c>doc-needed</c> —
/// which is exactly CHO's fail-closed answer and means it no longer has to
/// choose between silence and a claim it cannot support — and invariant
/// <c>crd-ci-q9</c> requires a <c>reason</c> whenever one is used, so the
/// "could not determine" answer must say why.
/// </para>
///
/// <para>
/// The invariants this construction respects, by never producing the shapes they
/// forbid: <c>q1</c> (no questionnaire without doc-needed — CHO names
/// questionnaires only on a documentation-required determination, which always
/// carries doc-needed), <c>q2</c> (no pa-needed under not-covered — CHO asserts
/// not-covered only for eligibility, never for a benefit exclusion, and omits
/// pa-needed there),
/// <c>q3</c> (covered is <c>conditional</c> only with info-needed
/// <c>contract-window</c>), <c>q5</c> (pa-needed <c>satisfied</c> exactly when a
/// satisfied-pa-id is emitted), <c>q4</c> (doc-purpose <c>withpa</c> is never paired with a
/// satisfied or no-auth determination), <c>q7</c> (every reason carries text),
/// <c>q8</c> and <c>q9</c> (a reason accompanies every doc-purpose and every
/// indeterminate). <c>CrdCoverageInformationTests</c> asserts each of those on
/// the emitted JSON rather than trusting this comment.
/// </para>
/// </summary>
public sealed record CrdCoverageInformation
{
    /// <summary>The extension's canonical URL, unversioned per FHIR convention.</summary>
    public const string ExtensionUrl =
        "http://hl7.org/fhir/us/davinci-crd/StructureDefinition/ext-coverage-information";

    /// <summary>The code system of the coverage-information detail codes (CRD 2.2.1 coverageDetailNew).</summary>
    public const string CoverageDetailCodeSystem = "http://terminology.hl7.org/CodeSystem/crd-coverage-detail";

    /// <summary>The IG's temp code system, which carries the Coverage Assertion Reasons.</summary>
    public const string TempCodeSystem = "http://hl7.org/fhir/us/davinci-crd/CodeSystem/temp";

    /// <summary>Reference to the Coverage the determination was made against, e.g. <c>Coverage/123</c>.</summary>
    public required string CoverageReference { get; init; }

    /// <summary>The payer's identifier for this assertion, quotable back to CHO.</summary>
    public required string CoverageAssertionId { get; init; }

    /// <summary>The date the assertion was made.</summary>
    public required DateOnly Date { get; init; }

    public required CrdDetermination Determination { get; init; }

    /// <summary>Required whenever the determination is indeterminate or carries a doc-purpose.</summary>
    public CrdIndeterminateReason? Reason { get; init; }

    /// <summary>Free text for the reason. Always present when <see cref="Reason"/> is.</summary>
    public string? ReasonText { get; init; }

    /// <summary>The billing code the determination applies to, when one was evaluated.</summary>
    public string? BillingCodeSystem { get; init; }
    public string? BillingCode { get; init; }

    /// <summary>
    /// Canonicals (<c>url|version</c>) of the DTR questionnaires that collect the
    /// documentation (CRD resp-38). Emitted only alongside doc-needed, so
    /// invariant crd-ci-q1 holds whatever a caller puts here.
    /// </summary>
    public IReadOnlyList<string> Questionnaires { get; init; } = [];

    /// <summary>
    /// The last day the assertion holds: CHO keeps it, and answers its
    /// coverage-assertion-id, until then (<c>Cms0057:Crd:AssertionRetentionDays</c>).
    /// </summary>
    public DateOnly? ExpiryDate { get; init; }

    /// <summary>Who to contact about the determination: the plan, by name, phone and website.</summary>
    public CrdPayerContact? Contact { get; init; }

    /// <summary>
    /// The payer's published coverage policy, emitted as a <c>policy-link</c>
    /// detail: the document the determination's basis is in.
    /// </summary>
    public string? PolicyUrl { get; init; }

    /// <summary>
    /// The approved authorization that satisfies the requirement. Emitted, with
    /// pa-needed <c>satisfied</c>, only on <see cref="CrdDetermination.AuthorizationSatisfied"/>.
    /// </summary>
    public string? SatisfiedPaId { get; init; }

    /// <summary>
    /// The order is planned for after the coverage period ends: covered is
    /// <c>conditional</c> and info-needed is <c>contract-window</c> (crd-ci-q3),
    /// whatever the plan's rules say about the service.
    /// </summary>
    public bool OutsideContractWindow { get; init; }

    /// <summary>
    /// Other orders in the same request this assertion depends on, as
    /// <c>{type}/{id}</c> (the <c>dependency</c> sub-extension): an order based
    /// on another draft order is covered as part of it.
    /// </summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];

    private bool Satisfied => Determination == CrdDetermination.AuthorizationSatisfied
                              && !string.IsNullOrWhiteSpace(SatisfiedPaId);

    /// <summary>Whether <see cref="OutsideContractWindow"/> applies: never under not-covered or indeterminate.</summary>
    private bool Conditional => OutsideContractWindow
                                && Determination is not (CrdDetermination.NotCovered or CrdDetermination.Indeterminate);

    /// <summary>`covered` / `not-covered` / `conditional` / `indeterminate`.</summary>
    public string Covered => Determination switch
    {
        CrdDetermination.Indeterminate => "indeterminate",
        CrdDetermination.NotCovered => "not-covered",
        _ when Conditional => "conditional",
        _ => "covered",
    };

    /// <summary>`no-auth` / `auth-needed` / `satisfied` / `performpa` / `conditional` / `indeterminate`.</summary>
    /// <remarks>Null under not-covered: crd-ci-q2 forbids pa-needed there.</remarks>
    public string? PaNeeded => Determination switch
    {
        CrdDetermination.NotCovered => null,
        CrdDetermination.AuthorizationRequired => "auth-needed",
        CrdDetermination.NoAuthorizationRequired => "no-auth",
        // q5: satisfied exactly when there is a satisfied-pa-id. Without the
        // number the requirement is still a requirement.
        CrdDetermination.AuthorizationSatisfied => Satisfied ? "satisfied" : "auth-needed",
        // CHO classified the service as needing documentation. That says nothing
        // about prior authorization, and under CHO's fail-closed policy silence
        // must not read as "none needed".
        CrdDetermination.DocumentationRequired => "indeterminate",
        _ => "indeterminate",
    };

    /// <summary>Serializes the extension as the object a system action's resource carries.</summary>
    public Dictionary<string, object?> ToExtension()
    {
        var parts = new List<Dictionary<string, object?>>
        {
            Part("coverage", "valueReference", new Dictionary<string, object?> { ["reference"] = CoverageReference }),
            Part("covered", "valueCode", Covered),
        };
        if (PaNeeded is { } paNeeded)
            parts.Add(Part("pa-needed", "valueCode", paNeeded));
        if (Satisfied)
            parts.Add(Part("satisfied-pa-id", "valueString", SatisfiedPaId!.Trim()));

        if (Determination == CrdDetermination.DocumentationRequired)
        {
            parts.Add(Part("doc-needed", "valueCode", "clinical"));
            // withorder, not withpa: CHO is asking for documentation to accompany
            // the order, and it has not asserted that prior authorization is
            // needed. withpa would also collide with crd-ci-q4 the moment the
            // pa-needed answer changed.
            parts.Add(Part("doc-purpose", "valueCode", "withorder"));

            // resp-38: name the forms, so a DTR client can ask for exactly
            // these by canonical or resolve them from coverage-assertion-id.
            foreach (var canonical in Questionnaires.Where(q => !string.IsNullOrWhiteSpace(q)).Distinct(StringComparer.Ordinal))
                parts.Add(Part("questionnaire", "valueCanonical", canonical));
        }

        if (BillingCode is not null)
        {
            parts.Add(Part("billingCode", "valueCoding", new Dictionary<string, object?>
            {
                ["system"] = BillingCodeSystem,
                ["code"] = BillingCode,
            }));
        }

        if (Conditional)
            parts.Add(Part("info-needed", "valueCode", "contract-window"));

        // A reason the determination needs comes first; otherwise a
        // conditional answer says why it is conditional.
        if ((Reason ?? (Conditional ? CrdIndeterminateReason.OutsideCoveragePeriod : null)) is { } reason)
        {
            parts.Add(Part("reason", "valueCodeableConcept", ReasonConcept(reason, ReasonText)));
        }

        foreach (var dependency in Dependencies.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.Ordinal))
            parts.Add(Part("dependency", "valueReference", new Dictionary<string, object?> { ["reference"] = dependency }));

        if (PolicyUrl is not null)
        {
            parts.Add(new Dictionary<string, object?>
            {
                ["url"] = "detail",
                ["extension"] = new[]
                {
                    Part("category", "valueCode", "cat-other"),
                    Part("code", "valueCodeableConcept", new Dictionary<string, object?>
                    {
                        ["coding"] = new[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["system"] = CoverageDetailCodeSystem,
                                ["code"] = "policy-link",
                                ["display"] = "Policy Link",
                            },
                        },
                    }),
                    Part("value", "valueUrl", PolicyUrl),
                },
            });
        }

        parts.Add(Part("date", "valueDate", Date.ToString("yyyy-MM-dd")));
        parts.Add(Part("coverage-assertion-id", "valueString", CoverageAssertionId));

        if (Contact?.ToContactDetail() is { } contact)
            parts.Add(Part("contact", "valueContactDetail", contact));

        if (ExpiryDate is { } expiry)
            parts.Add(Part("expiry-date", "valueDate", expiry.ToString("yyyy-MM-dd")));

        return new Dictionary<string, object?>
        {
            ["url"] = ExtensionUrl,
            ["extension"] = parts,
        };
    }

    private static Dictionary<string, object?> ReasonConcept(CrdIndeterminateReason reason, string? text)
    {
        var concept = new Dictionary<string, object?> { ["text"] = text ?? DefaultReasonText(reason) };

        // Several of CHO's reasons have a code in the IG's Coverage Assertion
        // Reasons value set, and those carry it. The rest carry text alone
        // rather than a coding CHO cannot justify — crd-ci-q7 is satisfied by
        // the text either way.
        if (IgReasonCode(reason) is { } code)
        {
            concept["coding"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["system"] = TempCodeSystem,
                    ["code"] = code.Code,
                    ["display"] = code.Display,
                },
            };
        }

        return concept;
    }

    /// <summary>
    /// The IG code for a reason, where the value set has one. Null where it does
    /// not: nothing in it means "this payer has no classification for the code",
    /// and inventing a code would be a wrong claim rather than a missing one.
    /// </summary>
    internal static (string Code, string Display)? IgReasonCode(CrdIndeterminateReason reason) => reason switch
    {
        CrdIndeterminateReason.Technical => ("technical", "Technical issues"),
        CrdIndeterminateReason.MemberNotFound => ("no-member-found", "Member not found"),
        CrdIndeterminateReason.NoActiveCoverage => ("no-active-coverage", "Coverage not active"),
        CrdIndeterminateReason.CoverageContextIncomplete => ("coverage-not-found", "Coverage not found"),
        CrdIndeterminateReason.CoverageNotFound => ("coverage-not-found", "Coverage not found"),
        _ => null,
    };

    /// <summary>The text a reason carries when the caller supplies none.</summary>
    public static string DefaultReasonText(CrdIndeterminateReason reason) => reason switch
    {
        CrdIndeterminateReason.Unclassified =>
            "Cloud Health Office has no coverage classification for this service, so it cannot state "
            + "whether prior authorization is required. This is not a determination that none is required.",
        CrdIndeterminateReason.Technical =>
            "Cloud Health Office could not evaluate coverage rules for this service. The problem may be "
            + "temporary; a later request may return a determination.",
        CrdIndeterminateReason.NoCodedService =>
            "The draft order carries no coded service, so there is nothing to evaluate coverage against.",
        CrdIndeterminateReason.DocumentationOnlyClassification =>
            "Cloud Health Office requires additional documentation for this service and has made no "
            + "determination about prior authorization.",
        CrdIndeterminateReason.MemberNotFound =>
            "Cloud Health Office could not identify the member, so it cannot apply this member's coverage "
            + "rules to the service.",
        CrdIndeterminateReason.NoActiveCoverage =>
            "Cloud Health Office holds no coverage in effect for this member, so it cannot state the "
            + "coverage requirements for the service.",
        CrdIndeterminateReason.CoverageContextIncomplete =>
            "Cloud Health Office could not determine the state and line of business this member's coverage "
            + "rules are selected by, so no rule set applies.",
        CrdIndeterminateReason.CoverageNotFound =>
            "The coverage on this order is not a coverage Cloud Health Office holds for this member.",
        CrdIndeterminateReason.OutsideCoveragePeriod =>
            "The order is planned for after the member's current coverage ends. Coverage for it depends on "
            + "coverage being in place on that date.",
        _ => "Cloud Health Office could not determine the coverage requirements for this service.",
    };

    private static Dictionary<string, object?> Part(string url, string valueProperty, object? value) =>
        new() { ["url"] = url, [valueProperty] = value };
}

/// <summary>
/// One CDS Hooks system action: a change the service asks the EHR to apply to a
/// resource in the hook context without the user having to accept a suggestion.
/// </summary>
public class CdsHooksSystemAction
{
    /// <summary>`create`, `update` or `delete`. CRD coverage-information is always an update.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "update";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("resource")]
    public JsonElement? Resource { get; set; }
}

/// <summary>The plan's contact details, as a coverage-information <c>contact</c>.</summary>
public sealed record CrdPayerContact(string? Name, string? Phone, string? Website)
{
    /// <summary>A FHIR ContactDetail, or null when there is nothing to contact.</summary>
    public Dictionary<string, object?>? ToContactDetail()
    {
        var telecom = new List<Dictionary<string, object?>>();
        if (!string.IsNullOrWhiteSpace(Phone))
            telecom.Add(new() { ["system"] = "phone", ["value"] = Phone.Trim() });
        if (!string.IsNullOrWhiteSpace(Website))
            telecom.Add(new() { ["system"] = "url", ["value"] = Website.Trim() });
        if (telecom.Count == 0)
            return null;

        var contact = new Dictionary<string, object?> { ["telecom"] = telecom };
        if (!string.IsNullOrWhiteSpace(Name))
            contact["name"] = Name.Trim();
        return contact;
    }
}
