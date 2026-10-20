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
    /// <summary>No <c>patient</c> reference.</summary>
    NoPatient,
    /// <summary>No HRex consent policy (<c>#regular</c> or <c>#sensitive</c>).</summary>
    NoPolicy,
    /// <summary>
    /// The <c>#regular</c> policy excludes sensitive data. CHO cannot segment
    /// sensitive data out of an export, so it cannot honour that consent.
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

    /// <summary>SHA-256 of the Consent as received, so a record can name the evidence without holding it.</summary>
    public string? Fingerprint { get; init; }

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
///   <item><description>if it lists actions, includes <c>disclose</c>.</description></item>
/// </list>
/// Parties are compared by identifier value, normalized as member identifiers are
/// (case, spaces and hyphens), from the actor's logical reference or a contained
/// Organization it points to.
/// </summary>
public static class PdexConsentValidator
{
    public const string RegularPolicy = "http://hl7.org/fhir/us/davinci-hrex/StructureDefinition-hrex-consent.html#regular";
    public const string SensitivePolicy = "http://hl7.org/fhir/us/davinci-hrex/StructureDefinition-hrex-consent.html#sensitive";
    public const string InformationRecipientRole = "IRCP";

    public static PdexConsentValidation Validate(
        JsonNode? consentNode, string receivingPayerId, string localPayerId, DateTime nowUtc)
    {
        if (consentNode is null) return PdexConsentValidation.Fail(PdexConsentFailure.Missing);
        if (consentNode is not JsonObject consent || Str(consent["resourceType"]) != "Consent")
            return PdexConsentValidation.Fail(PdexConsentFailure.NotAConsent);

        if (Str(consent["status"]) != "active") return PdexConsentValidation.Fail(PdexConsentFailure.NotActive);
        if (!Codes(consent["scope"]).Contains("patient-privacy"))
            return PdexConsentValidation.Fail(PdexConsentFailure.WrongScope);
        if (!(consent["category"] as JsonArray ?? []).SelectMany(Codes).Contains("IDSCL"))
            return PdexConsentValidation.Fail(PdexConsentFailure.WrongCategory);
        if (consent["patient"] is not JsonObject patient
            || (string.IsNullOrWhiteSpace(Str(patient["reference"])) && patient["identifier"] is null))
            return PdexConsentValidation.Fail(PdexConsentFailure.NoPatient);

        var policies = (consent["policy"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(p => Str(p["uri"])).Where(u => u is not null).ToList();
        string policy;
        if (policies.Contains(SensitivePolicy)) policy = SensitivePolicy;
        else if (policies.Contains(RegularPolicy)) return PdexConsentValidation.Fail(PdexConsentFailure.SegmentationUnsupported);
        else return PdexConsentValidation.Fail(PdexConsentFailure.NoPolicy);

        if (consent["provision"] is not JsonObject provision || Str(provision["type"]) != "permit")
            return PdexConsentValidation.Fail(PdexConsentFailure.NotPermit);

        if (provision["period"] is not JsonObject period
            || !TryInstant(Str(period["start"]), out var start)
            || (period["end"] is not null && !TryInstant(Str(period["end"]), out _)))
            return PdexConsentValidation.Fail(PdexConsentFailure.NotInForce);
        DateTime? end = TryInstant(Str(period["end"]), out var e) ? e : null;
        if (nowUtc < start || (end is { } until && nowUtc >= until))
            return PdexConsentValidation.Fail(PdexConsentFailure.NotInForce);

        if (provision["action"] is JsonArray actions && actions.Count > 0
            && !actions.SelectMany(Codes).Contains("disclose"))
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
            if (Codes(actor["role"]).Contains(InformationRecipientRole) && parties.Contains(recipient))
                recipientBound = true;
        }
        if (!recipientBound)
            return PdexConsentValidation.Fail(PdexConsentFailure.RecipientNotBound);

        return new PdexConsentValidation
        {
            Valid = true,
            Policy = policy,
            PeriodStartUtc = start,
            PeriodEndUtc = end,
            Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(consent.ToJsonString()))).ToLowerInvariant(),
        };
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

    private static IEnumerable<string> Codes(JsonNode? concept) =>
        (concept?["coding"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(c => Str(c["code"])).Where(c => c is not null)!;

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool TryInstant(string? value, out DateTime instant)
    {
        instant = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        // A FHIR date (yyyy-MM-dd) or dateTime; a date-only value is midnight UTC.
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return false;
        instant = parsed.UtcDateTime;
        return true;
    }
}

/// <summary>
/// Builds the Consent CHO sends when it is the requesting payer (outbound,
/// P2P-02): the member's opt-in recorded in CHO's registry, as an HRex consent
/// naming CHO as information recipient and the prior payer as source.
/// </summary>
public static class PdexConsentBuilder
{
    public static JsonObject Build(
        string patientReference, string recipientPayerId, string sourcePayerId, DateTime startUtc, DateTime? endUtc)
    {
        var period = new JsonObject { ["start"] = startUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) };
        if (endUtc is { } end) period["end"] = end.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        return new JsonObject
        {
            ["resourceType"] = "Consent",
            ["status"] = "active",
            ["scope"] = Concept("http://terminology.hl7.org/CodeSystem/consentscope", "patient-privacy"),
            ["category"] = new JsonArray(Concept("http://terminology.hl7.org/CodeSystem/v3-ActCode", "IDSCL")),
            ["patient"] = new JsonObject { ["reference"] = patientReference },
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
