using System.Text.Json.Nodes;
using ClaimsService.Models;

namespace ClaimsService.Fhir;

/// <summary>
/// The CHO claim record as a FHIR R4 Claim: what the provider billed.
///
/// <para>
/// Claim and ExplanationOfBenefit are not alternatives. A Claim carries the
/// request — lines, units, charges, diagnoses, the prior authorization cited —
/// and an EOB carries what adjudication did with it. CHO projects both from the
/// same record and gives them the same chain-stable id
/// (<see cref="Claim.ClaimVersionId"/>), so <c>Claim/{id}</c> and
/// <c>ExplanationOfBenefit/{id}</c> are the two faces of one claim. Nothing
/// adjudication decided appears here: no allowed amount, no payment, no patient
/// responsibility, no denial reason. A consumer that wants those reads the EOB.
/// </para>
///
/// <para>
/// Not a profiled resource. No profile is declared and the elements CARIN Blue
/// Button or Da Vinci PAS would require are not all present: the insurer is a
/// display string rather than an Organization reference, the provider and the
/// payee are NPI-identifier references rather than resolvable resources, there
/// is no care team, and <c>priority</c> is always <c>normal</c> because the
/// stored record does not carry a billed priority. See docs/epa-gap-matrix.md
/// (G-14, G-60) and docs/architecture/claim-as-billed-fhir-projection.md.
/// </para>
/// </summary>
public sealed class ClaimFhirProjector : IClaimFhirProjector
{
    public const string ClaimNumberSystem = "urn:cho:claim-number";
    public const string ClaimTypeSystem = "http://terminology.hl7.org/CodeSystem/claim-type";
    public const string ProcessPrioritySystem = "http://terminology.hl7.org/CodeSystem/processpriority";
    public const string NpiSystem = "http://hl7.org/fhir/sid/us-npi";
    public const string Icd10CmSystem = "http://hl7.org/fhir/sid/icd-10-cm";
    public const string Cpt4System = "http://www.ama-assn.org/go/cpt";
    public const string ModifierSystem = "http://www.ama-assn.org/go/cpt";
    public const string RevenueCodeSystem = "https://www.nubc.org/CodeSystem/RevenueCodes";
    public const string NdcSystem = "http://hl7.org/fhir/sid/ndc";
    public const string PlaceOfServiceSystem = "https://www.cms.gov/Medicare/Coding/place-of-service-codes";
    public const string DiagnosisTypeSystem = "http://terminology.hl7.org/CodeSystem/ex-diagnosistype";

    /// <summary>
    /// CMS place-of-service code (837 CLM05-1). FHIR R4 Claim has no element
    /// for it at header level, so it is carried as an extension rather than
    /// dropped; a line that overrides it carries its own
    /// <c>item.locationCodeableConcept</c>, which FHIR does define.
    /// </summary>
    public const string PlaceOfServiceExtension = "urn:cho:StructureDefinition:claim-place-of-service";

    public JsonObject Project(Claim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);

        // Same chain-stable id the EOB projection uses, so the two projections
        // of one claim share an id. Hydrate aliases legacy rows.
        var fhirId = string.IsNullOrEmpty(claim.ClaimVersionId) ? claim.Id : claim.ClaimVersionId;

        var resource = new JsonObject
        {
            ["resourceType"] = "Claim",
            ["id"] = fhirId,
            ["meta"] = new JsonObject { ["lastUpdated"] = AsUtc(claim.LastUpdatedDate).ToString("o") },
            ["identifier"] = new JsonArray
            {
                new JsonObject
                {
                    ["system"] = ClaimNumberSystem,
                    ["value"] = claim.ClaimNumber,
                },
            },
            ["status"] = StatusOf(claim.Status),
            ["type"] = TypeOf(claim.ClaimType),
            // FHIR Claim.use is the purpose of the request. Every record this
            // projection serves is a real claim for adjudication; a
            // preauthorization request is a prior-authorization record, which
            // authorization-service projects separately (PDex EOB).
            ["use"] = "claim",
            ["patient"] = new JsonObject { ["reference"] = "Patient/" + claim.MemberId },
            ["created"] = AsUtc(claim.SubmittedDate).ToString("o"),
            // priority is 1..1 in FHIR R4 and the stored record carries no
            // billed priority, so it is stated as normal rather than guessed.
            ["priority"] = Concept(ProcessPrioritySystem, "normal", "Normal"),
            ["billablePeriod"] = new JsonObject
            {
                ["start"] = AsUtc(claim.ServiceDateFrom).ToString("o"),
                ["end"] = AsUtc(claim.ServiceDateTo).ToString("o"),
            },
            ["insurance"] = Insurance(claim),
            ["total"] = Money(claim.TotalChargeAmount),
        };

        if (!string.IsNullOrWhiteSpace(claim.BillingProviderNPI))
            resource["provider"] = NpiReference(claim.BillingProviderNPI);

        if (Diagnoses(claim) is { Count: > 0 } diagnoses)
            resource["diagnosis"] = diagnoses;

        if (Items(claim) is { Count: > 0 } items)
            resource["item"] = items;

        if (claim.PlaceOfServiceCode?.Trim() is { Length: > 0 } pos)
        {
            resource["extension"] = new JsonArray
            {
                new JsonObject
                {
                    ["url"] = PlaceOfServiceExtension,
                    ["valueCoding"] = new JsonObject
                    {
                        ["system"] = PlaceOfServiceSystem,
                        ["code"] = pos,
                    },
                },
            };
        }

        return resource;
    }

    /// <summary>
    /// FHIR <c>Claim.status</c> is an <c>fm-status</c> code about the resource,
    /// not an adjudication outcome: a claim is <c>active</c> while it stands and
    /// <c>cancelled</c> once it is voided. Adjudication state (pended, denied,
    /// paid) is not expressible here and is not implied — it is on the EOB.
    /// </summary>
    /// <para>
    /// Internal rather than private because <c>ClaimFhirSearchRepository</c>'s
    /// status filter has to be its exact inverse, and that agreement is worth
    /// asserting against this method rather than a copy of its rule
    /// (InternalsVisibleTo covers the test project).
    /// </para>
    internal static string StatusOf(ClaimStatus status) => status switch
    {
        ClaimStatus.Voided => "cancelled",
        _ => "active",
    };

    private static JsonObject TypeOf(ClaimType type) => type switch
    {
        ClaimType.Institutional => Concept(ClaimTypeSystem, "institutional", "Institutional"),
        ClaimType.Dental => Concept(ClaimTypeSystem, "oral", "Oral Health"),
        _ => Concept(ClaimTypeSystem, "professional", "Professional"),
    };

    /// <summary>
    /// The coverage the claim was billed against, and the prior authorization
    /// it cited. <c>insurance</c> is 1..* in FHIR R4, so one entry is always
    /// emitted; when the record names no coverage the reference is an
    /// identifier-free display, which says "this plan" without inventing a
    /// Coverage id that would not resolve.
    /// </summary>
    private static JsonArray Insurance(Claim claim)
    {
        var entry = new JsonObject
        {
            ["sequence"] = 1,
            ["focal"] = true,
        };

        entry["coverage"] = string.IsNullOrWhiteSpace(claim.CoverageId)
            ? new JsonObject { ["display"] = claim.LineOfBusiness.ToString() + " coverage" }
            : new JsonObject { ["reference"] = "Coverage/" + claim.CoverageId };

        if (!string.IsNullOrWhiteSpace(claim.PriorAuthorizationNumber))
            entry["preAuthRef"] = new JsonArray { claim.PriorAuthorizationNumber };

        return new JsonArray { entry };
    }

    private static JsonArray Diagnoses(Claim claim)
    {
        var coded = claim.DiagnosisCodes
            .Where(d => !string.IsNullOrWhiteSpace(d.Code))
            .OrderBy(d => d.PointerNumber)
            .ToList();

        // FHIR diagnosis.sequence is what item.diagnosisSequence points at, so
        // it must be the 837 pointer number the lines were built with. Use the
        // stored pointers only when they can serve as keys — all present and
        // all distinct; otherwise fall back to ordinal numbering, which keeps
        // the resource internally consistent even though the line pointers
        // will then match nothing.
        var usePointers = coded.Count > 0
            && coded.All(d => d.PointerNumber > 0)
            && coded.Select(d => d.PointerNumber).Distinct().Count() == coded.Count;

        var diagnoses = new JsonArray();
        var sequence = 0;
        foreach (var diagnosis in coded)
        {
            sequence++;
            var pointer = usePointers ? diagnosis.PointerNumber : sequence;

            var coding = new JsonObject
            {
                ["system"] = Icd10CmSystem,
                ["code"] = diagnosis.Code,
            };
            if (!string.IsNullOrWhiteSpace(diagnosis.Description)) coding["display"] = diagnosis.Description;

            var entry = new JsonObject
            {
                ["sequence"] = pointer,
                ["diagnosisCodeableConcept"] = new JsonObject { ["coding"] = new JsonArray { coding } },
            };

            // ABK is the 837's principal diagnosis qualifier; every other
            // qualifier the record can carry is a secondary one.
            entry["type"] = new JsonArray
            {
                string.Equals(diagnosis.CodeQualifier, "ABK", StringComparison.OrdinalIgnoreCase)
                    ? Concept(DiagnosisTypeSystem, "principal", "Principal Diagnosis")
                    : Concept(DiagnosisTypeSystem, "secondary", "Secondary Diagnosis"),
            };

            diagnoses.Add(entry);
        }
        return diagnoses;
    }

    private static JsonArray Items(Claim claim)
    {
        var items = new JsonArray();
        foreach (var line in claim.ClaimLines.OrderBy(l => l.LineNumber))
        {
            var product = new JsonObject
            {
                ["system"] = Cpt4System,
                ["code"] = line.ProcedureCode,
            };
            if (!string.IsNullOrWhiteSpace(line.ProcedureDescription)) product["display"] = line.ProcedureDescription;

            var item = new JsonObject
            {
                ["sequence"] = line.LineNumber,
                ["productOrService"] = new JsonObject { ["coding"] = new JsonArray { product } },
                ["servicedPeriod"] = new JsonObject
                {
                    ["start"] = AsUtc(line.ServiceDateFrom).ToString("o"),
                    ["end"] = AsUtc(line.ServiceDateTo).ToString("o"),
                },
                ["quantity"] = new JsonObject
                {
                    ["value"] = JsonValue.Create(line.Units),
                },
                // net is the line's total billed charge (837 SV102/SV203), not
                // a unit price, so unitPrice is deliberately absent rather than
                // set to net and implicitly multiplied by quantity.
                ["net"] = Money(line.ChargeAmount),
            };

            if (line.Modifiers.Where(m => !string.IsNullOrWhiteSpace(m)).ToList() is { Count: > 0 } modifiers)
            {
                item["modifier"] = new JsonArray(modifiers
                    .Select(m => (JsonNode)Concept(ModifierSystem, m, null))
                    .ToArray());
            }

            if (line.DiagnosisPointers.Where(p => p > 0).Distinct().ToList() is { Count: > 0 } pointers)
                item["diagnosisSequence"] = new JsonArray(pointers.Select(p => (JsonNode)JsonValue.Create(p)).ToArray());

            // A line that overrides the claim's place of service says so where
            // FHIR defines it.
            if (line.PlaceOfServiceCode?.Trim() is { Length: > 0 } pos
                && !string.Equals(pos, claim.PlaceOfServiceCode?.Trim(), StringComparison.Ordinal))
            {
                item["locationCodeableConcept"] = Concept(PlaceOfServiceSystem, pos, null);
            }

            if (!string.IsNullOrWhiteSpace(line.RevenueCode))
                item["revenue"] = Concept(RevenueCodeSystem, line.RevenueCode, null);

            if (!string.IsNullOrWhiteSpace(line.NationalDrugCode))
            {
                var detail = new JsonObject
                {
                    ["sequence"] = 1,
                    ["productOrService"] = Concept(NdcSystem, line.NationalDrugCode, null),
                };
                if (line.DrugQuantity is { } drugQuantity)
                {
                    var quantity = new JsonObject { ["value"] = JsonValue.Create(drugQuantity) };
                    if (!string.IsNullOrWhiteSpace(line.DrugUnitOfMeasure))
                        quantity["code"] = line.DrugUnitOfMeasure;
                    detail["quantity"] = quantity;
                }
                item["detail"] = new JsonArray { detail };
            }

            items.Add(item);
        }
        return items;
    }

    private static JsonObject NpiReference(string npi) => new()
    {
        ["identifier"] = new JsonObject
        {
            ["system"] = NpiSystem,
            ["value"] = npi,
        },
    };

    private static JsonObject Concept(string system, string code, string? display)
    {
        var coding = new JsonObject
        {
            ["system"] = system,
            ["code"] = code,
        };
        if (!string.IsNullOrWhiteSpace(display)) coding["display"] = display;
        return new JsonObject { ["coding"] = new JsonArray { coding } };
    }

    private static JsonObject Money(decimal amount) => new()
    {
        ["value"] = JsonValue.Create(amount),
        ["currency"] = "USD",
    };

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
