using System.Text.Json.Nodes;

namespace FhirService.Services.PayerToPayer;

/// <summary>
/// Keeps only the claim itself in a Payer-to-Payer ExplanationOfBenefit (ADR 020).
/// CMS-0057-F excludes provider remittances and enrollee cost-sharing, and CHO's
/// internal adjudication (examiner and AI dispositions in <c>supportingInfo</c>)
/// is not the receiving payer's either.
///
/// This is an allowlist, so an element the projector adds later stays home
/// until it is listed here. What leaves: who (patient, provider, care team,
/// facility, insurer), when (billable period, service dates), what (type,
/// diagnoses, procedures, and each item, detail and sub-detail's service codes,
/// modifiers, quantity and place), the coverage it ran under and its prior
/// authorization reference. What stays: every amount (total, payment, unit
/// price, net, factor, benefit balance), every adjudication with its CARC/RARC
/// reasons, the payee, the disposition text and process notes, added items,
/// supporting information, the narrative, extensions and contained resources.
/// </summary>
public static class PayerToPayerEobRedaction
{
    private static readonly HashSet<string> Header = new(StringComparer.Ordinal)
    {
        "resourceType", "id", "meta", "identifier", "status", "type", "subType", "use", "patient",
        "billablePeriod", "created", "enterer", "insurer", "provider", "priority", "related", "prescription",
        "originalPrescription", "referral", "facility", "claim", "outcome", "preAuthRef", "preAuthRefPeriod",
        "careTeam", "diagnosis", "procedure", "precedence", "insurance", "accident", "item",
    };

    private static readonly HashSet<string> Item = new(StringComparer.Ordinal)
    {
        "sequence", "careTeamSequence", "diagnosisSequence", "procedureSequence", "revenue", "category",
        "productOrService", "modifier", "programCode", "servicedDate", "servicedPeriod", "locationCodeableConcept",
        "locationAddress", "locationReference", "quantity", "udi", "bodySite", "subSite", "encounter", "detail",
    };

    private static readonly HashSet<string> Detail = new(StringComparer.Ordinal)
    {
        "sequence", "revenue", "category", "productOrService", "modifier", "programCode", "quantity", "udi", "subDetail",
    };

    private static readonly HashSet<string> SubDetail = new(StringComparer.Ordinal)
    {
        "sequence", "revenue", "category", "productOrService", "modifier", "programCode", "quantity", "udi",
    };

    private static readonly HashSet<string> Insurance = new(StringComparer.Ordinal) { "focal", "coverage", "preAuthRef" };

    private static readonly HashSet<string> Meta = new(StringComparer.Ordinal) { "lastUpdated", "profile" };

    /// <summary>A redacted copy of <paramref name="eob"/>; the original is untouched.</summary>
    public static JsonObject Redact(JsonObject eob)
    {
        var copy = Keep(eob, Header);
        if (copy["meta"] is JsonObject meta) copy["meta"] = Keep(meta, Meta);
        if (copy["insurance"] is JsonArray insurance) copy["insurance"] = KeepEach(insurance, Insurance, null);
        if (copy["item"] is JsonArray items)
        {
            copy["item"] = KeepEach(items, Item, item =>
            {
                if (item["detail"] is JsonArray details)
                {
                    item["detail"] = KeepEach(details, Detail, detail =>
                    {
                        if (detail["subDetail"] is JsonArray subDetails)
                            detail["subDetail"] = KeepEach(subDetails, SubDetail, null);
                    });
                }
            });
        }
        return copy;
    }

    private static JsonObject Keep(JsonObject source, HashSet<string> allowed)
    {
        var kept = new JsonObject();
        foreach (var (key, value) in source)
            if (allowed.Contains(key)) kept[key] = value?.DeepClone();
        return kept;
    }

    private static JsonArray KeepEach(JsonArray source, HashSet<string> allowed, Action<JsonObject>? children)
    {
        var kept = new JsonArray();
        foreach (var element in source.OfType<JsonObject>())
        {
            var copy = Keep(element, allowed);
            children?.Invoke(copy);
            kept.Add(copy);
        }
        return kept;
    }
}
