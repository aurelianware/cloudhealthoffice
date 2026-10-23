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
/// forbid: <c>q1</c> (no questionnaire without doc-needed — CHO emits no
/// questionnaire), <c>q2</c> (no pa-needed under not-covered — CHO never asserts
/// not-covered, because it does not evaluate benefit exclusions here),
/// <c>q3</c>/<c>q5</c> (CHO emits neither <c>conditional</c> nor
/// <c>satisfied</c>), <c>q4</c> (doc-purpose <c>withpa</c> is never paired with a
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

    /// <summary>`covered` / `not-covered` / `conditional` / `indeterminate`.</summary>
    public string Covered => Determination == CrdDetermination.Indeterminate ? "indeterminate" : "covered";

    /// <summary>`no-auth` / `auth-needed` / `satisfied` / `performpa` / `conditional` / `indeterminate`.</summary>
    public string PaNeeded => Determination switch
    {
        CrdDetermination.AuthorizationRequired => "auth-needed",
        CrdDetermination.NoAuthorizationRequired => "no-auth",
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
            Part("pa-needed", "valueCode", PaNeeded),
        };

        if (Determination == CrdDetermination.DocumentationRequired)
        {
            parts.Add(Part("doc-needed", "valueCode", "clinical"));
            // withorder, not withpa: CHO is asking for documentation to accompany
            // the order, and it has not asserted that prior authorization is
            // needed. withpa would also collide with crd-ci-q4 the moment the
            // pa-needed answer changed.
            parts.Add(Part("doc-purpose", "valueCode", "withorder"));
        }

        if (BillingCode is not null)
        {
            parts.Add(Part("billingCode", "valueCoding", new Dictionary<string, object?>
            {
                ["system"] = BillingCodeSystem,
                ["code"] = BillingCode,
            }));
        }

        if (Reason is { } reason)
        {
            parts.Add(Part("reason", "valueCodeableConcept", ReasonConcept(reason, ReasonText)));
        }

        parts.Add(Part("date", "valueDate", Date.ToString("yyyy-MM-dd")));
        parts.Add(Part("coverage-assertion-id", "valueString", CoverageAssertionId));

        return new Dictionary<string, object?>
        {
            ["url"] = ExtensionUrl,
            ["extension"] = parts,
        };
    }

    private static Dictionary<string, object?> ReasonConcept(CrdIndeterminateReason reason, string? text)
    {
        var concept = new Dictionary<string, object?> { ["text"] = text ?? DefaultReasonText(reason) };

        // Only one of CHO's reasons has a code in the IG's value set. The rest
        // carry text alone rather than a coding CHO cannot justify.
        if (reason == CrdIndeterminateReason.Technical)
        {
            concept["coding"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["system"] = TempCodeSystem,
                    ["code"] = "technical",
                    ["display"] = "Technical issues",
                },
            };
        }

        return concept;
    }

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
