using System.Text.Json.Nodes;

namespace FhirService.Services.PayerToPayer.BulkMemberMatch;

/// <summary>
/// One <c>MemberBundle</c> of a PDex <c>$bulk-member-match</c> request: the
/// member as the requesting payer submitted it, and the attributes CHO matches on.
/// </summary>
public sealed class BulkMemberMatchEntry
{
    /// <summary>The submitted <c>MemberPatient</c>, echoed back in the result Group.</summary>
    public required JsonObject MemberPatient { get; init; }

    /// <summary>The submitted <c>CoverageToMatch</c>: the member's coverage with this plan.</summary>
    public required JsonObject CoverageToMatch { get; init; }

    /// <summary>The member's HRex consent, when the bundle carried one.</summary>
    public JsonObject? Consent { get; init; }

    public string? FamilyName { get; init; }
    public string? GivenName { get; init; }
    public string? BirthDate { get; init; }
    public string? Gender { get; init; }

    /// <summary>
    /// The member's id with this plan: the Patient's member (<c>MB</c>)
    /// identifier, else the coverage's subscriber id.
    /// </summary>
    public string? MemberId { get; init; }

    public string? SubscriberId { get; init; }
    public string? PostalCode { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }

    /// <summary>When the coverage to match ended (or began, when it has not), as yyyy-MM-dd.</summary>
    public string? AsOfDate { get; init; }
}

/// <summary>
/// Reads the PDex <c>$bulk-member-match</c> input (PDex 2.1
/// <c>PDexMultiMemberMatchRequestParameters</c>): a FHIR Parameters resource
/// with one <c>MemberBundle</c> per member, each with the parts
/// <c>MemberPatient</c> (1..1), <c>CoverageToMatch</c> (1..1),
/// <c>CoverageToLink</c> (0..1) and <c>Consent</c> (1..1).
///
/// A request that is not that shape is refused whole (PDex: 422). A missing
/// <c>Consent</c> is not a shape error: it is that member's consent failing, so
/// the member is judged on its own and the rest of the request is not lost.
/// </summary>
public static class BulkMemberMatchRequestReader
{
    private const string MemberIdentifierType = "MB";

    public static bool TryRead(
        JsonObject? body, int maxMembers, out IReadOnlyList<BulkMemberMatchEntry> entries, out string? error)
    {
        entries = [];
        error = null;

        if (body is null || Str(body["resourceType"]) != "Parameters")
        {
            error = "The request body must be a FHIR Parameters resource.";
            return false;
        }

        var bundles = (body["parameter"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(p => Str(p["name"]) == "MemberBundle")
            .ToList();
        if (bundles.Count == 0)
        {
            error = "At least one MemberBundle parameter is required.";
            return false;
        }
        if (bundles.Count > maxMembers)
        {
            error = $"A request may carry at most {maxMembers} MemberBundle parameters; split larger requests.";
            return false;
        }

        var read = new List<BulkMemberMatchEntry>(bundles.Count);
        for (var i = 0; i < bundles.Count; i++)
        {
            var parts = (bundles[i]["part"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            var patient = Part(parts, "MemberPatient", "Patient");
            var coverage = Part(parts, "CoverageToMatch", "Coverage");
            if (patient is null || coverage is null)
            {
                error = $"MemberBundle {i + 1} must carry a MemberPatient (Patient) and a CoverageToMatch (Coverage).";
                return false;
            }
            read.Add(Member(patient, coverage, Part(parts, "Consent", "Consent")));
        }

        entries = read;
        return true;
    }

    /// <summary>
    /// Reads the single-member HRex <c>Patient/$member-match</c> input
    /// (<c>hrex-parameters-member-match-in</c>): the same <c>MemberPatient</c>,
    /// <c>CoverageToMatch</c>, optional <c>CoverageToLink</c> and
    /// <c>Consent</c> parameters a <c>MemberBundle</c> carries, at the top
    /// level of the Parameters. Read exactly as one bundle of the bulk form is,
    /// so the two operations cannot match the same member differently.
    /// </summary>
    public static bool TryReadSingle(JsonObject? body, out BulkMemberMatchEntry? entry, out string? error)
    {
        entry = null;
        error = null;

        if (body is null || Str(body["resourceType"]) != "Parameters")
        {
            error = "The request body must be a FHIR Parameters resource.";
            return false;
        }

        var parts = (body["parameter"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var patient = Part(parts, "MemberPatient", "Patient");
        var coverage = Part(parts, "CoverageToMatch", "Coverage");
        if (patient is null || coverage is null)
        {
            error = "A member-match request must carry a MemberPatient (Patient) and a CoverageToMatch (Coverage).";
            return false;
        }

        entry = Member(patient, coverage, Part(parts, "Consent", "Consent"));
        return true;
    }

    private static BulkMemberMatchEntry Member(JsonObject patient, JsonObject coverage, JsonObject? consent)
    {
        var name = Names(patient).FirstOrDefault(n => Str(n["use"]) == "official") ?? Names(patient).FirstOrDefault();
        var subscriberId = Str(coverage["subscriberId"]);
        var period = coverage["period"] as JsonObject;

        return new BulkMemberMatchEntry
        {
            MemberPatient = patient,
            CoverageToMatch = coverage,
            Consent = WithMemberPatient(consent, patient),
            FamilyName = Str(name?["family"]),
            GivenName = Str((name?["given"] as JsonArray)?.FirstOrDefault()),
            BirthDate = Str(patient["birthDate"]),
            Gender = Str(patient["gender"]),
            MemberId = MemberIdentifier(patient) ?? subscriberId,
            SubscriberId = subscriberId,
            PostalCode = (patient["address"] as JsonArray)?.OfType<JsonObject>()
                .Select(a => Str(a["postalCode"])).FirstOrDefault(p => p is not null),
            Phone = Telecom(patient, "phone"),
            Email = Telecom(patient, "email"),
            AsOfDate = DatePart(Str(period?["end"]) ?? Str(period?["start"])),
        };
    }

    /// <summary>The id a <c>MemberPatient</c> is contained under when the Consent names it.</summary>
    internal const string ContainedMemberPatientId = "hrex-member-patient";

    /// <summary>
    /// HRex requests name the member in the Consent by a local reference to the
    /// <c>MemberPatient</c> parameter (<c>Patient/1</c> for a MemberPatient with
    /// id 1), and the Inferno PDex kit requires exactly that. The consent check
    /// reads the member only from a contained Patient or an identifier, so such
    /// a Consent is judged on a copy with the MemberPatient contained and the
    /// reference pointing at it. Nothing is added that the request did not say:
    /// the Patient is the one the request supplied, and a Consent naming any
    /// other reference is left as it came.
    /// </summary>
    internal static JsonObject? WithMemberPatient(JsonObject? consent, JsonObject patient)
    {
        if (consent?["patient"] is not JsonObject subject
            || Str(subject["reference"]) is not { } reference
            || Str(patient["id"]) is not { } id
            || reference != $"Patient/{id}")
        {
            return consent;
        }

        var copy = (JsonObject)consent.DeepClone();
        var contained = (JsonObject)patient.DeepClone();
        contained["id"] = ContainedMemberPatientId;
        if (copy["contained"] is not JsonArray list)
        {
            list = new JsonArray();
            copy["contained"] = list;
        }
        list.Add(contained);
        copy["patient"] = new JsonObject { ["reference"] = $"#{ContainedMemberPatientId}" };
        return copy;
    }

    private static JsonObject? Part(List<JsonObject> parts, string name, string resourceType)
        => parts.Where(p => Str(p["name"]) == name)
            .Select(p => p["resource"] as JsonObject)
            .FirstOrDefault(r => r is not null && Str(r["resourceType"]) == resourceType);

    private static IEnumerable<JsonObject> Names(JsonObject patient)
        => (patient["name"] as JsonArray ?? []).OfType<JsonObject>();

    /// <summary>The value of the Patient's identifier typed <c>MB</c> (member number).</summary>
    private static string? MemberIdentifier(JsonObject patient)
        => (patient["identifier"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(id => (id["type"]?["coding"] as JsonArray ?? []).OfType<JsonObject>()
                .Any(c => Str(c["code"]) == MemberIdentifierType))
            .Select(id => Str(id["value"]))
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string? Telecom(JsonObject patient, string system)
        => (patient["telecom"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(t => Str(t["system"]) == system)
            .Select(t => Str(t["value"]))
            .FirstOrDefault(v => v is not null);

    private static string? DatePart(string? value)
        => value is { Length: >= 10 } ? value[..10] : null;

    private static string? Str(JsonNode? node)
        => node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}
