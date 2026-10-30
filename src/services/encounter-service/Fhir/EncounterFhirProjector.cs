using System.Text.Json.Nodes;
using EncounterService.Models;

namespace EncounterService.Fhir;

/// <summary>
/// The CHO encounter submission record as a FHIR R4 Encounter.
///
/// <para>
/// What this resource asserts, and what it does not. The stored record is an
/// 837 submission envelope: it carries the real-world contact (who was seen,
/// when, by whom, where, for what) and, separately, the lifecycle of sending
/// that contact to a payer. Only the first of those is a FHIR Encounter, so
/// <c>status</c> is the state of the <em>contact</em> — the service was
/// rendered, hence <c>finished</c>, or the submitter withdrew the assertion,
/// hence <c>entered-in-error</c>. It is deliberately not the EDI submission
/// state: an encounter still queued for dispatch describes a visit that
/// already happened, and projecting <c>planned</c> for it would misstate the
/// clinical fact. The submission lifecycle stays on the service's own API.
/// </para>
///
/// <para>
/// Not US Core. No profile is declared and the elements US Core requires of an
/// Encounter are not all present: participants and the service provider are
/// carried as NPI-identifier references rather than resolvable
/// Practitioner/Organization resources (CHO has no FHIR identity for them on
/// this path), there is no <c>location</c>, and <c>type</c> uses a CHO-private
/// code system because the 837 transaction variant has no US Core equivalent.
/// Diagnoses travel as <c>reasonCode</c> rather than <c>diagnosis</c>, which
/// would need Condition resources CHO does not project. See
/// docs/epa-gap-matrix.md (G-14) and docs/architecture/encounter-fhir-projection.md.
/// </para>
/// </summary>
public sealed class EncounterFhirProjector : IEncounterFhirProjector
{
    /// <summary>CHO's encounter control number, the 837 batch tracking id.</summary>
    public const string ControlNumberSystem = "urn:cho:encounter-control-number";

    /// <summary>
    /// The 837 transaction variant (professional / institutional / dental).
    /// CHO-private: it describes which EDI form the contact was billed on,
    /// which no HL7 Encounter.type value set covers.
    /// </summary>
    public const string EncounterTypeSystem = "urn:cho:encounter-type";

    public const string ActCodeSystem = "http://terminology.hl7.org/CodeSystem/v3-ActCode";
    public const string NpiSystem = "http://hl7.org/fhir/sid/us-npi";
    public const string Icd10CmSystem = "http://hl7.org/fhir/sid/icd-10-cm";

    /// <summary>
    /// CMS place-of-service code (837 CLM05-1). FHIR R4 Encounter has no
    /// element for it — <c>class</c> is a single Coding and <c>location</c>
    /// needs a Location resource CHO does not project — so it is carried as an
    /// extension rather than dropped. The code it narrows is in <c>class</c>.
    /// </summary>
    public const string PlaceOfServiceExtension = "urn:cho:StructureDefinition:encounter-place-of-service";

    public const string PlaceOfServiceSystem = "https://www.cms.gov/Medicare/Coding/place-of-service-codes/Place_of_Service_Code_Set";

    /// <summary>
    /// Place-of-service codes whose encounter class is not ambulatory. Only
    /// codes with an unambiguous v3-ActCode counterpart are listed; everything
    /// else projects as <c>AMB</c>, which is what an outpatient professional
    /// encounter — the common case — is.
    /// </summary>
    private static readonly Dictionary<string, (string Code, string Display)> ClassByPlaceOfService = new(StringComparer.Ordinal)
    {
        ["12"] = ("HH", "home health"),
        ["21"] = ("IMP", "inpatient encounter"),
        ["23"] = ("EMER", "emergency"),
        ["31"] = ("IMP", "inpatient encounter"),
        ["32"] = ("IMP", "inpatient encounter"),
        ["51"] = ("IMP", "inpatient encounter"),
        ["55"] = ("IMP", "inpatient encounter"),
        ["56"] = ("IMP", "inpatient encounter"),
        ["61"] = ("IMP", "inpatient encounter"),
    };

    /// <summary>The class every place of service outside <see cref="ClassByPlaceOfService"/> gets.</summary>
    public const string DefaultClass = "AMB";

    /// <summary>
    /// The places of service that project as <paramref name="classCode"/>, the
    /// inverse of <see cref="ClassOf"/>. <see cref="DefaultClass"/> returns
    /// the mapped codes it excludes instead (<c>Excluding</c> true), since
    /// every other place of service, or none, projects as it. Any other code
    /// returns null: the projection never emits it.
    /// </summary>
    public static (IReadOnlyList<string> PlacesOfService, bool Excluding)? PlacesOfServiceFor(string classCode)
    {
        if (string.Equals(classCode, DefaultClass, StringComparison.Ordinal))
            return (ClassByPlaceOfService.Keys.ToList(), true);
        var places = ClassByPlaceOfService.Where(p => string.Equals(p.Value.Code, classCode, StringComparison.Ordinal))
            .Select(p => p.Key).ToList();
        return places.Count == 0 ? null : (places, false);
    }

    public JsonObject Project(Encounter encounter)
    {
        ArgumentNullException.ThrowIfNull(encounter);

        var resource = new JsonObject
        {
            ["resourceType"] = "Encounter",
            ["id"] = encounter.Id,
            // meta.lastUpdated drives FHIR _lastUpdated search, as it does on
            // every other CHO projection. ISO 8601 with offset.
            ["meta"] = new JsonObject { ["lastUpdated"] = AsUtc(encounter.LastUpdatedDate).ToString("o") },
            ["status"] = StatusOf(encounter),
            ["class"] = ClassOf(encounter),
            ["subject"] = new JsonObject { ["reference"] = "Patient/" + encounter.MemberId },
            ["period"] = Period(encounter.ServiceDateFrom, encounter.ServiceDateTo),
        };

        if (!string.IsNullOrWhiteSpace(encounter.EncounterControlNumber))
        {
            resource["identifier"] = new JsonArray
            {
                new JsonObject
                {
                    ["system"] = ControlNumberSystem,
                    ["value"] = encounter.EncounterControlNumber,
                },
            };
        }

        resource["type"] = new JsonArray { TypeOf(encounter.EncounterType) };

        if (Participants(encounter) is { Count: > 0 } participants)
            resource["participant"] = participants;

        if (!string.IsNullOrWhiteSpace(encounter.BillingProviderNPI))
        {
            resource["serviceProvider"] = Organization(encounter.BillingProviderNPI, encounter.BillingProviderName);
        }

        if (ReasonCodes(encounter) is { Count: > 0 } reasons)
            resource["reasonCode"] = reasons;

        if (encounter.PlaceOfServiceCode?.Trim() is { Length: > 0 } pos)
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
    /// The state of the real-world contact. A void — by submission type, by
    /// 837 CLM05-3 frequency code 8, or by the record reaching Voided —
    /// withdraws the assertion that the contact happened.
    ///
    /// <para>
    /// Public because <c>EncounterFhirSearchRepository</c>'s status filter has
    /// to be its exact inverse, and that agreement is worth asserting against
    /// this method rather than against a copy of its rule.
    /// </para>
    /// </summary>
    public static string StatusOf(Encounter encounter) =>
        encounter.SubmissionType == SubmissionType.Void
        || encounter.Status == EncounterStatus.Voided
        || string.Equals(encounter.ClaimFrequencyCode, "8", StringComparison.Ordinal)
            ? FhirEncounterStatusCodes.EnteredInError
            : FhirEncounterStatusCodes.Finished;

    private static JsonObject ClassOf(Encounter encounter)
    {
        var pos = encounter.PlaceOfServiceCode?.Trim() ?? string.Empty;
        var (code, display) = ClassByPlaceOfService.TryGetValue(pos, out var mapped)
            ? mapped
            : (DefaultClass, "ambulatory");

        // Encounter.class is a single Coding, so the place of service it was
        // derived from cannot ride along in it; it is carried verbatim in the
        // PlaceOfServiceExtension instead of being dropped.
        return new JsonObject
        {
            ["system"] = ActCodeSystem,
            ["code"] = code,
            ["display"] = display,
        };
    }

    private static JsonObject TypeOf(EncounterType type) => new()
    {
        ["coding"] = new JsonArray
        {
            new JsonObject
            {
                ["system"] = EncounterTypeSystem,
                ["code"] = type switch
                {
                    EncounterType.Institutional => "institutional",
                    EncounterType.Dental => "dental",
                    _ => "professional",
                },
                ["display"] = type switch
                {
                    EncounterType.Institutional => "Institutional (837I)",
                    EncounterType.Dental => "Dental (837D)",
                    _ => "Professional (837P)",
                },
            },
        },
    };

    /// <summary>
    /// The rendering provider, when the record names one distinct from the
    /// biller. Carried as an identifier-only reference: CHO has no FHIR
    /// Practitioner id for a provider on this path, and a fabricated
    /// <c>Practitioner/{npi}</c> reference would not resolve.
    /// </summary>
    private static JsonArray Participants(Encounter encounter)
    {
        var participants = new JsonArray();
        if (string.IsNullOrWhiteSpace(encounter.RenderingProviderNPI)) return participants;

        participants.Add(new JsonObject
        {
            ["type"] = new JsonArray
            {
                new JsonObject
                {
                    ["coding"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["system"] = "http://terminology.hl7.org/CodeSystem/v3-ParticipationType",
                            ["code"] = "PPRF",
                            ["display"] = "primary performer",
                        },
                    },
                },
            },
            ["individual"] = new JsonObject
            {
                ["identifier"] = new JsonObject
                {
                    ["system"] = NpiSystem,
                    ["value"] = encounter.RenderingProviderNPI,
                },
            },
        });
        return participants;
    }

    private static JsonObject Organization(string npi, string? name)
    {
        var reference = new JsonObject
        {
            ["identifier"] = new JsonObject
            {
                ["system"] = NpiSystem,
                ["value"] = npi,
            },
        };
        if (!string.IsNullOrWhiteSpace(name)) reference["display"] = name;
        return reference;
    }

    /// <summary>
    /// The encounter's diagnoses. FHIR <c>Encounter.diagnosis</c> references
    /// Condition resources, which CHO does not project on this path, so the
    /// codes travel as <c>reasonCode</c> — a CodeableConcept, which carries
    /// them without asserting a Condition exists.
    /// </summary>
    private static JsonArray ReasonCodes(Encounter encounter)
    {
        var reasons = new JsonArray();
        foreach (var diagnosis in encounter.DiagnosisCodes.OrderBy(d => d.PointerNumber))
        {
            if (string.IsNullOrWhiteSpace(diagnosis.Code)) continue;
            var coding = new JsonObject
            {
                ["system"] = Icd10CmSystem,
                ["code"] = diagnosis.Code,
            };
            if (!string.IsNullOrWhiteSpace(diagnosis.Description)) coding["display"] = diagnosis.Description;
            reasons.Add(new JsonObject { ["coding"] = new JsonArray { coding } });
        }
        return reasons;
    }

    private static JsonObject Period(DateTime from, DateTime to) => new()
    {
        ["start"] = AsUtc(from).ToString("o"),
        ["end"] = AsUtc(to).ToString("o"),
    };

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
