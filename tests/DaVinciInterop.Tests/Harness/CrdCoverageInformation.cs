using System.Text.Json;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// The Da Vinci CRD coverage-information extension, as returned by a CRD server on
/// the resource carried in a CDS Hooks system action.
///
/// This is where a CRD server states its actual determination. A scenario that
/// inspected only `cards` would miss the decision entirely: the pinned HL7
/// reference implementation answers an order-sign hook with zero cards and one
/// system action whose extension carries the whole answer.
///
/// The parser reads what is present and does not invent defaults. An absent
/// `pa-needed` means the server said nothing about prior authorization, which is
/// materially different from saying no authorization is required — so it is
/// modelled as null, never as "no-auth".
/// </summary>
public sealed record CrdCoverageInformation
{
    /// <summary>The canonical URL of the extension in the CRD IG (unversioned, per FHIR convention).</summary>
    public const string ExtensionUrl =
        "http://hl7.org/fhir/us/davinci-crd/StructureDefinition/ext-coverage-information";

    /// <summary>Reference to the Coverage the determination was made against.</summary>
    public string? CoverageReference { get; init; }

    /// <summary>`covered` / `not-covered` / `conditional` — the coverage determination.</summary>
    public string? Covered { get; init; }

    /// <summary>`auth-needed` / `no-auth` / `satisfied` / `performpa` — the prior-authorization determination.</summary>
    public string? PaNeeded { get; init; }

    /// <summary>`clinical` / `admin` / `patient` / `no-doc` — documentation requirement.</summary>
    public string? DocNeeded { get; init; }

    /// <summary>Further information the payer needs, e.g. `detail-code`.</summary>
    public string? InfoNeeded { get; init; }

    /// <summary>Canonical of a DTR questionnaire the payer wants completed, when it names one.</summary>
    public string? QuestionnaireCanonical { get; init; }

    /// <summary>The billing code the determination applies to.</summary>
    public string? BillingCodeSystem { get; init; }
    public string? BillingCode { get; init; }

    /// <summary>The payer's identifier for this assertion, quotable back to the payer.</summary>
    public string? CoverageAssertionId { get; init; }

    public string? Date { get; init; }

    /// <summary>Sub-extension urls seen, so an unexpected one is visible rather than silently dropped.</summary>
    public IReadOnlyList<string> PresentFields { get; init; } = Array.Empty<string>();

    /// <summary>True when the payer stated that prior authorization is required.</summary>
    public bool IsPriorAuthRequired => PaNeeded == "auth-needed";

    /// <summary>True when the payer stated the service is not covered.</summary>
    public bool IsNotCovered => Covered == "not-covered";

    /// <summary>
    /// A one-line, PHI-free summary of the determination, safe for evidence.
    /// Carries only coded determinations and the billing code — no patient,
    /// practitioner or clinical narrative.
    /// </summary>
    public string SafeSummary()
    {
        var parts = new List<string> { $"covered={Covered ?? "(absent)"}" };
        if (PaNeeded is not null) parts.Add($"pa-needed={PaNeeded}");
        if (DocNeeded is not null) parts.Add($"doc-needed={DocNeeded}");
        if (InfoNeeded is not null) parts.Add($"info-needed={InfoNeeded}");
        if (QuestionnaireCanonical is not null) parts.Add("questionnaire=present");
        if (BillingCode is not null) parts.Add($"billingCode={BillingCode}");
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Extracts every coverage-information extension from a CDS Hooks response's
    /// system actions. Returns an empty list when the server attached none — which
    /// the caller must treat as "the payer made no coverage statement", not as a
    /// negative determination.
    /// </summary>
    public static IReadOnlyList<CrdCoverageInformation> FromSystemActions(CdsHooksResponse response)
    {
        var results = new List<CrdCoverageInformation>();
        foreach (var action in response.SystemActions ?? new List<CdsHooksSystemAction>())
        {
            if (action.Resource is not { ValueKind: JsonValueKind.Object } resource
                || !resource.TryGetProperty("extension", out var extensions)
                || extensions.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var extension in extensions.EnumerateArray())
            {
                if (!extension.TryGetProperty("url", out var url)
                    || url.ValueKind != JsonValueKind.String
                    || url.GetString() != ExtensionUrl)
                {
                    continue;
                }

                var parsed = Parse(extension);
                if (parsed is not null)
                {
                    results.Add(parsed);
                }
            }
        }

        return results;
    }

    private static CrdCoverageInformation? Parse(JsonElement extension)
    {
        if (!extension.TryGetProperty("extension", out var parts) || parts.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? coverage = null, covered = null, paNeeded = null, docNeeded = null, infoNeeded = null;
        string? questionnaire = null, billingSystem = null, billingCode = null, assertionId = null, date = null;
        var present = new List<string>();

        foreach (var part in parts.EnumerateArray())
        {
            if (!part.TryGetProperty("url", out var urlElement) || urlElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = urlElement.GetString()!;
            present.Add(name);

            switch (name)
            {
                case "coverage":
                    coverage = String(part, "valueReference", "reference");
                    break;
                case "covered":
                    covered = Code(part);
                    break;
                case "pa-needed":
                    paNeeded = Code(part);
                    break;
                case "doc-needed":
                    docNeeded = Code(part);
                    break;
                case "info-needed":
                    infoNeeded = Code(part);
                    break;
                case "questionnaire":
                    questionnaire = Scalar(part, "valueCanonical");
                    break;
                case "billingCode":
                    billingSystem = String(part, "valueCoding", "system");
                    billingCode = String(part, "valueCoding", "code");
                    break;
                case "coverage-assertion-id":
                    assertionId = Scalar(part, "valueString");
                    break;
                case "date":
                    date = Scalar(part, "valueDate");
                    break;
            }
        }

        return new CrdCoverageInformation
        {
            CoverageReference = coverage,
            Covered = covered,
            PaNeeded = paNeeded,
            DocNeeded = docNeeded,
            InfoNeeded = infoNeeded,
            QuestionnaireCanonical = questionnaire,
            BillingCodeSystem = billingSystem,
            BillingCode = billingCode,
            CoverageAssertionId = assertionId,
            Date = date,
            PresentFields = present,
        };
    }

    private static string? Code(JsonElement part) =>
        Scalar(part, "valueCode") ?? String(part, "valueCodeableConcept", "coding");

    private static string? Scalar(JsonElement part, string property) =>
        part.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? String(JsonElement part, string container, string property)
    {
        if (!part.TryGetProperty(container, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (value.TryGetProperty(property, out var inner))
        {
            if (inner.ValueKind == JsonValueKind.String)
            {
                return inner.GetString();
            }

            // valueCodeableConcept.coding[0].code, for servers that use a
            // CodeableConcept where others use a plain code.
            if (inner.ValueKind == JsonValueKind.Array)
            {
                foreach (var coding in inner.EnumerateArray())
                {
                    if (coding.ValueKind == JsonValueKind.Object
                        && coding.TryGetProperty("code", out var code)
                        && code.ValueKind == JsonValueKind.String)
                    {
                        return code.GetString();
                    }
                }
            }
        }

        return null;
    }
}
