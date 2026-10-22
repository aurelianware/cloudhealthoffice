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

    // ── Prior authorization (use = preauthorization) ─────────────────────────
    // A separate allowlist, deliberately not a widening of the claim one: what a
    // receiving payer needs from a prior authorization is almost the opposite of
    // what it needs from a claim. The decision IS the payload — a payer taking
    // over a member has to know what was approved, for what period, and why
    // anything was refused, so that it can honour an existing authorization
    // rather than make the member start again. On a claim those same fields
    // (disposition, process notes, item adjudication) are CHO's internal
    // adjudication and money, and stay home.
    //
    // Nothing monetary is listed here, and the projection carries no amounts to
    // begin with: a prior-auth EOB has units, not dollars. Cost-sharing and
    // remittance therefore cannot leave by this path.

    private static readonly HashSet<string> PriorAuthHeader = new(StringComparer.Ordinal)
    {
        "resourceType", "id", "meta", "identifier", "status", "type", "subType", "use", "patient",
        "created", "insurer", "provider", "outcome", "disposition", "preAuthRef", "preAuthRefPeriod",
        "billablePeriod", "benefitPeriod", "diagnosis", "procedure", "insurance", "item", "processNote",
    };

    private static readonly HashSet<string> PriorAuthItem = new(StringComparer.Ordinal)
    {
        "sequence", "revenue", "category", "productOrService", "modifier", "programCode",
        "servicedDate", "servicedPeriod", "locationCodeableConcept", "locationAddress",
        "quantity", "adjudication",
    };

    /// <summary>
    /// A redacted copy of a prior-authorization <c>ExplanationOfBenefit</c>
    /// (<c>use = preauthorization</c>); the original is untouched. Use this for
    /// prior authorizations and <see cref="Redact"/> for claims — the two
    /// allowlists are not interchangeable.
    /// </summary>
    public static JsonObject RedactPriorAuthorization(JsonObject eob)
    {
        var copy = Keep(eob, PriorAuthHeader);
        if (copy["meta"] is JsonObject meta) copy["meta"] = Keep(meta, Meta);
        if (copy["insurance"] is JsonArray insurance) copy["insurance"] = KeepEach(insurance, Insurance, null);
        if (copy["item"] is JsonArray items) copy["item"] = KeepEach(items, PriorAuthItem, null);
        return copy;
    }

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
