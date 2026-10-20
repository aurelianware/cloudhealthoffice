using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace FhirService.Services.PayerToPayer;

/// <summary>Why a Payer-to-Payer consent was not accepted (ADR 022). Closed set, no PHI.</summary>
public enum PdexConsentFailure
{
    /// <summary>No Consent resource was supplied.</summary>
    Missing,
    /// <summary>The value is not a FHIR Consent resource.</summary>
    NotAConsent,
    /// <summary><c>status</c> is not <c>active</c>.</summary>
    NotActive,
    /// <summary><c>scope</c> is not <c>patient-privacy</c>.</summary>
    WrongScope,
    /// <summary><c>category</c> does not include <c>IDSCL</c> (information disclosure).</summary>
    WrongCategory,
    /// <summary>No <c>patient</c> element.</summary>
    NoPatient,
    /// <summary>The Consent's patient is not the member matched or exported (ADR 022).</summary>
    PatientNotBound,
    /// <summary>No HRex consent policy (<c>#regular</c> or <c>#sensitive</c>).</summary>
    NoPolicy,
    /// <summary>
    /// The consent restricts what may be disclosed: the <c>#regular</c> policy
    /// (no sensitive data), or a nested provision, class, code, data reference or
    /// security label. CHO cannot segment an export, so it cannot honour it.
    /// </summary>
    SegmentationUnsupported,
    /// <summary><c>provision.type</c> is not <c>permit</c>.</summary>
    NotPermit,
    /// <summary><c>provision.period</c> is missing, malformed, or does not cover now.</summary>
    NotInForce,
    /// <summary>No information recipient (IRCP actor) is the requesting payer.</summary>
    RecipientNotBound,
    /// <summary>An actor names a party that is neither the requesting payer nor this plan.</summary>
    UnknownParty,
    /// <summary><c>provision.action</c> is present and does not include <c>disclose</c>.</summary>
    ActionNotDisclose,
}

/// <summary>The outcome of checking a Payer-to-Payer consent against the request it arrived with.</summary>
public sealed record PdexConsentValidation
{
    public required bool Valid { get; init; }
    public PdexConsentFailure? Failure { get; init; }
    public string? Policy { get; init; }
    public DateTime? PeriodStartUtc { get; init; }
    public DateTime? PeriodEndUtc { get; init; }

    /// <summary>SHA-256 of the Consent (as parsed), so a record can name the evidence without holding it.</summary>
    public string? Fingerprint { get; init; }

    /// <summary>The patient's identifiers, normalized as member identifiers are.</summary>
    public IReadOnlySet<string> PatientIdentifiers { get; init; } = new HashSet<string>();

    /// <summary>The contained Patient's family name and birth date, normalized, when given.</summary>
    public string? PatientFamily { get; init; }
    public string? PatientBirthDate { get; init; }

    /// <summary>
    /// Whether the Consent's patient is this member: one of its identifiers is one
    /// the member bears (their member id, or a subscriber id on their coverage), or
    /// its contained Patient's family name and birth date both agree with theirs.
    /// </summary>
    public bool NamesMember(IEnumerable<string?> memberIdentifiers, string? family, string? birthDate)
    {
        var ids = memberIdentifiers.Select(MemberIdentityNormalizer.Identifier).Where(i => i is not null).ToHashSet();
        if (PatientIdentifiers.Overlaps(ids!)) return true;
        return PatientFamily is not null && PatientBirthDate is not null
               && PatientFamily == MemberIdentityNormalizer.Name(family)
               && PatientBirthDate == MemberIdentityNormalizer.BirthDate(birthDate);
    }

    public static PdexConsentValidation Fail(PdexConsentFailure failure) => new() { Valid = false, Failure = failure };
}

/// <summary>
/// Checks the member's Payer-to-Payer consent that a requesting payer sends with
/// <c>$member-match</c> (Da Vinci PDex / HRex <c>hrex-consent</c>; ADR 022).
/// Under CMS-0057-F the requesting (new) payer collects the member's opt-in; the
/// responding plan honours it only when the Consent:
/// <list type="bullet">
///   <item><description>is an active patient-privacy disclosure (<c>IDSCL</c>) permit
///     for a patient;</description></item>
///   <item><description>carries the HRex <c>#sensitive</c> policy. <c>#regular</c>
///     asks the plan to withhold sensitive data, which CHO cannot segment, so it is
///     refused rather than over-disclosed;</description></item>
///   <item><description>is in force now (<c>provision.period</c>: started, and not
///     ended when an end is given);</description></item>
///   <item><description>names the requesting payer, the one the authenticated
///     client is registered to, as an information recipient (IRCP actor);</description></item>
///   <item><description>names no other party than that payer and this plan;</description></item>
///   <item><description>if it lists actions, includes <c>disclose</c>;</description></item>
///   <item><description>restricts nothing CHO would have to segment out (no nested
///     provision, class, code, data or security label).</description></item>
/// </list>
/// Its patient must be named by an identifier or a contained Patient; the caller
/// binds it to the member (<see cref="PdexConsentValidation.NamesMember"/>).
/// Parties are compared by identifier value, normalized as member identifiers are
/// (case, spaces and hyphens), from the actor's logical reference or a contained
/// Organization it points to.
/// </summary>
public static class PdexConsentValidator
{
    public const string RegularPolicy = "http://hl7.org/fhir/us/davinci-hrex/StructureDefinition-hrex-consent.html#regular";
    public const string SensitivePolicy = "http://hl7.org/fhir/us/davinci-hrex/StructureDefinition-hrex-consent.html#sensitive";
    public const string InformationRecipientRole = "IRCP";

    /// <summary>Clock skew tolerated on the consent's start between payers.</summary>
    public static readonly TimeSpan StartSkew = TimeSpan.FromMinutes(5);

    private const string ConsentScopeSystem = "http://terminology.hl7.org/CodeSystem/consentscope";
    private const string ActCodeSystem = "http://terminology.hl7.org/CodeSystem/v3-ActCode";
    private const string ParticipationSystem = "http://terminology.hl7.org/CodeSystem/v3-ParticipationType";

    public static PdexConsentValidation Validate(
        JsonNode? consentNode, string receivingPayerId, string localPayerId, DateTime nowUtc)
    {
        try
        {
            return ValidateCore(consentNode, receivingPayerId, localPayerId, nowUtc);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            // A malformed object (a repeated property, a value of the wrong
            // kind) is not a consent; it never becomes a 500.
            return PdexConsentValidation.Fail(PdexConsentFailure.NotAConsent);
        }
    }

    private static PdexConsentValidation ValidateCore(
        JsonNode? consentNode, string receivingPayerId, string localPayerId, DateTime nowUtc)
    {
        if (consentNode is null) return PdexConsentValidation.Fail(PdexConsentFailure.Missing);
        if (consentNode is not JsonObject consent || Str(consent["resourceType"]) != "Consent")
            return PdexConsentValidation.Fail(PdexConsentFailure.NotAConsent);

        if (Str(consent["status"]) != "active") return PdexConsentValidation.Fail(PdexConsentFailure.NotActive);
        if (!Codes(consent["scope"], ConsentScopeSystem).Contains("patient-privacy"))
            return PdexConsentValidation.Fail(PdexConsentFailure.WrongScope);
        if (!(consent["category"] as JsonArray ?? []).SelectMany(c => Codes(c, ActCodeSystem)).Contains("IDSCL"))
            return PdexConsentValidation.Fail(PdexConsentFailure.WrongCategory);

        // The patient must be present; whether it is THIS member is decided by
        // the caller once the member is known (NamesMember), so a consent that
        // names no one resolvable simply binds to no one.
        if (consent["patient"] is not JsonObject)
            return PdexConsentValidation.Fail(PdexConsentFailure.NoPatient);
        var (patientIds, family, birthDate) = Patient(consent);

        var policies = (consent["policy"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(p => Str(p["uri"])).Where(u => u is not null).ToList();
        if (policies.Contains(RegularPolicy))
            return PdexConsentValidation.Fail(PdexConsentFailure.SegmentationUnsupported);
        if (!policies.Contains(SensitivePolicy))
            return PdexConsentValidation.Fail(PdexConsentFailure.NoPolicy);

        if (consent["provision"] is not JsonObject provision || Str(provision["type"]) != "permit")
            return PdexConsentValidation.Fail(PdexConsentFailure.NotPermit);

        // Anything that narrows what may be disclosed would have to be segmented
        // out of the export, which CHO cannot do.
        foreach (var restriction in new[] { "provision", "class", "code", "data", "securityLabel", "dataPeriod" })
            if (provision[restriction] is JsonArray { Count: > 0 } or JsonObject)
                return PdexConsentValidation.Fail(PdexConsentFailure.SegmentationUnsupported);

        if (provision["period"] is not JsonObject period
            || !FhirInstant.TryStart(Str(period["start"]), out var start)
            || (period["end"] is not null && !FhirInstant.TryEnd(Str(period["end"]), out _)))
            return PdexConsentValidation.Fail(PdexConsentFailure.NotInForce);
        DateTime? end = FhirInstant.TryEnd(Str(period["end"]), out var e) ? e : null;
        if (nowUtc + StartSkew < start || (end is { } until && nowUtc >= until))
            return PdexConsentValidation.Fail(PdexConsentFailure.NotInForce);

        if (provision["action"] is JsonArray actions && actions.Count > 0
            && !actions.SelectMany(a => Codes(a, "http://terminology.hl7.org/CodeSystem/consentaction")).Contains("disclose"))
            return PdexConsentValidation.Fail(PdexConsentFailure.ActionNotDisclose);

        if (MemberIdentityNormalizer.Identifier(receivingPayerId) is not { } recipient)
            return PdexConsentValidation.Fail(PdexConsentFailure.RecipientNotBound);
        var local = MemberIdentityNormalizer.Identifier(localPayerId);
        var recipientBound = false;
        foreach (var actor in (provision["actor"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var parties = PartyIdentifiers(consent, actor["reference"]).ToList();
            if (parties.Count == 0) return PdexConsentValidation.Fail(PdexConsentFailure.UnknownParty);
            if (parties.Any(p => p != recipient && p != local))
                return PdexConsentValidation.Fail(PdexConsentFailure.UnknownParty);
            if (Codes(actor["role"], ParticipationSystem).Contains(InformationRecipientRole) && parties.Contains(recipient))
                recipientBound = true;
        }
        if (!recipientBound)
            return PdexConsentValidation.Fail(PdexConsentFailure.RecipientNotBound);

        return new PdexConsentValidation
        {
            Valid = true,
            Policy = SensitivePolicy,
            PeriodStartUtc = start,
            PeriodEndUtc = end,
            Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(consent.ToJsonString()))).ToLowerInvariant(),
            PatientIdentifiers = patientIds,
            PatientFamily = family,
            PatientBirthDate = birthDate,
        };
    }

    /// <summary>The patient's identifiers, and a contained Patient's family name and birth date.</summary>
    private static (HashSet<string> Ids, string? Family, string? BirthDate) Patient(JsonObject consent)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (consent["patient"] is not JsonObject patient) return (ids, null, null);
        if (MemberIdentityNormalizer.Identifier(Str(patient["identifier"]?["value"])) is { } direct) ids.Add(direct);

        string? family = null, birthDate = null;
        if (Str(patient["reference"]) is { } reference && reference.StartsWith('#'))
        {
            var contained = (consent["contained"] as JsonArray ?? []).OfType<JsonObject>()
                .FirstOrDefault(c => Str(c["id"]) == reference[1..] && Str(c["resourceType"]) == "Patient");
            foreach (var id in (contained?["identifier"] as JsonArray ?? []).OfType<JsonObject>())
                if (MemberIdentityNormalizer.Identifier(Str(id["value"])) is { } value) ids.Add(value);
            family = MemberIdentityNormalizer.Name(Str(contained?["name"]?[0]?["family"]));
            birthDate = MemberIdentityNormalizer.BirthDate(Str(contained?["birthDate"]));
        }
        return (ids, family, birthDate);
    }

    /// <summary>The identifier values a reference resolves to: its logical identifier, or a contained Organization's.</summary>
    private static IEnumerable<string> PartyIdentifiers(JsonObject consent, JsonNode? reference)
    {
        if (reference is not JsonObject r) yield break;
        if (MemberIdentityNormalizer.Identifier(Str(r["identifier"]?["value"])) is { } direct) yield return direct;
        if (Str(r["reference"]) is { } local && local.StartsWith('#'))
        {
            var contained = (consent["contained"] as JsonArray ?? []).OfType<JsonObject>()
                .FirstOrDefault(c => Str(c["id"]) == local[1..]);
            foreach (var id in (contained?["identifier"] as JsonArray ?? []).OfType<JsonObject>())
                if (MemberIdentityNormalizer.Identifier(Str(id["value"])) is { } value) yield return value;
        }
    }

    /// <summary>Codes of a CodeableConcept whose system is <paramref name="system"/> or not given.</summary>
    private static IEnumerable<string> Codes(JsonNode? concept, string system) =>
        (concept?["coding"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(c => Str(c["system"]) is null || Str(c["system"]) == system)
            .Select(c => Str(c["code"])).Where(c => c is not null)!;

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>
/// FHIR <c>date</c> / <c>dateTime</c> values as period bounds. A partial value
/// (year, year-month, date) starts at the beginning of its span and, as an end,
/// runs through the end of it, as FHIR periods are inclusive. A dateTime must
/// carry a zone. Anything else is unreadable.
/// </summary>
internal static class FhirInstant
{
    private static readonly System.Text.RegularExpressions.Regex Shape = new(
        @"^(?<y>\d{4})(-(?<m>\d{2})(-(?<d>\d{2})(?<t>T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2}))?)?)?$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static bool TryStart(string? value, out DateTime instant) => TryParse(value, end: false, out instant);

    public static bool TryEnd(string? value, out DateTime instant) => TryParse(value, end: true, out instant);

    private static bool TryParse(string? value, bool end, out DateTime instant)
    {
        instant = default;
        if (string.IsNullOrWhiteSpace(value) || Shape.Match(value) is not { Success: true } m) return false;
        try
        {
            if (m.Groups["t"].Success)
            {
                if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) return false;
                instant = at.UtcDateTime;
                return true;
            }
            var year = int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
            var month = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 1;
            var day = m.Groups["d"].Success ? int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture) : 1;
            var begin = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
            instant = !end ? begin
                : m.Groups["d"].Success ? begin.AddDays(1)
                : m.Groups["m"].Success ? begin.AddMonths(1)
                : begin.AddYears(1);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}

/// <summary>
/// Builds the Consent CHO sends when it is the requesting payer (outbound,
/// P2P-02): the member's opt-in recorded in CHO's registry, as an HRex consent
/// naming CHO as information recipient and the prior payer as source.
/// </summary>
public static class PdexConsentBuilder
{
    /// <summary>
    /// The consent, with the member as a contained Patient: their identifier at
    /// the recipient's counterpart (when known), family name and birth date, so the
    /// peer can bind it to the member it matches. The start is backdated by the
    /// tolerated clock skew.
    /// </summary>
    public static JsonObject Build(
        string? patientIdentifier, string recipientPayerId, string sourcePayerId, DateTime startUtc, DateTime? endUtc,
        string? family = null, string? birthDate = null)
    {
        var start = startUtc - PdexConsentValidator.StartSkew;
        var period = new JsonObject { ["start"] = start.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) };
        if (endUtc is { } end) period["end"] = end.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        var patient = new JsonObject { ["resourceType"] = "Patient", ["id"] = "member" };
        if (!string.IsNullOrWhiteSpace(patientIdentifier))
            patient["identifier"] = new JsonArray(new JsonObject { ["value"] = patientIdentifier });
        if (!string.IsNullOrWhiteSpace(family))
            patient["name"] = new JsonArray(new JsonObject { ["family"] = family });
        if (!string.IsNullOrWhiteSpace(birthDate)) patient["birthDate"] = birthDate;

        return new JsonObject
        {
            ["resourceType"] = "Consent",
            ["contained"] = new JsonArray(patient),
            ["status"] = "active",
            ["scope"] = Concept("http://terminology.hl7.org/CodeSystem/consentscope", "patient-privacy"),
            ["category"] = new JsonArray(Concept("http://terminology.hl7.org/CodeSystem/v3-ActCode", "IDSCL")),
            ["patient"] = new JsonObject { ["reference"] = "#member" },
            ["policy"] = new JsonArray(new JsonObject { ["uri"] = PdexConsentValidator.SensitivePolicy }),
            ["provision"] = new JsonObject
            {
                ["type"] = "permit",
                ["period"] = period,
                ["actor"] = new JsonArray(
                    Actor("PERF", sourcePayerId),
                    Actor(PdexConsentValidator.InformationRecipientRole, recipientPayerId)),
                ["action"] = new JsonArray(Concept("http://terminology.hl7.org/CodeSystem/consentaction", "disclose")),
            },
        };
    }

    private static JsonObject Concept(string system, string code) =>
        new() { ["coding"] = new JsonArray(new JsonObject { ["system"] = system, ["code"] = code }) };

    private static JsonObject Actor(string role, string payerId) => new()
    {
        ["role"] = Concept("http://terminology.hl7.org/CodeSystem/v3-ParticipationType", role),
        ["reference"] = new JsonObject { ["identifier"] = new JsonObject { ["value"] = payerId } },
    };
}
