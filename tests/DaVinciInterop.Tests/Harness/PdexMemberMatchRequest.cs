using System.Text.Json.Nodes;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// The HRex <c>$member-match</c> requests the PDex payer-server suite sends
/// CHO (its <c>member_match_request</c> and <c>no_member_match_request</c>
/// inputs), built from the seed so they name the member CHO actually holds.
///
/// <para>
/// The kit is the new payer and CHO the old one, so <c>CoverageToMatch</c> is
/// the member's coverage with CHO: its <c>subscriberId</c> is the seeded member
/// id, and its payor is fhir-service's local payer. <c>MemberPatient</c>
/// carries the demographics a new payer would have (name, birth date,
/// gender) and no identifier of CHO's, because a new payer does not know it.
/// </para>
///
/// <para>
/// The Consent is HRex's: patient-privacy scope, IDSCL category, the
/// <c>#sensitive</c> policy, a permit from CHO's payer (source) to the kit's
/// registered payer (information recipient), and the member named by a local
/// reference to the <c>MemberPatient</c> parameter, which the kit's own
/// "only uses local references" test requires. Nothing here is weaker than
/// what CHO demands of any requesting payer (ADR 022).
/// </para>
/// </summary>
public static class PdexMemberMatchRequest
{
    /// <summary>The payer id fhir-service answers as (<c>PayerToPayer:LocalPayerId</c>, its default).</summary>
    public const string ChoPayerId = "cloud-health-office";

    /// <summary>The subscriber id of a request that must match no one.</summary>
    public const string UnknownSubscriberId = "INTEROP-NO-SUCH-MEMBER";

    /// <summary>When the consent and the coverage start: the seeded coverage's effective date.</summary>
    public const string Start = "2026-01-01";

    /// <summary>A request for exactly the seeded member.</summary>
    public static JsonObject OneMatch(ChoSeedFixture fixture) => Build(
        fixture.PayerBackendClient.PayerId,
        fixture.Member.FirstName,
        fixture.Member.LastName,
        DatePart(fixture.Member.DateOfBirth),
        Gender(fixture.Member.Gender),
        fixture.Member.MemberId);

    /// <summary>A well-formed, consented request for a person CHO does not hold.</summary>
    public static JsonObject NoMatch(ChoSeedFixture fixture) => Build(
        fixture.PayerBackendClient.PayerId,
        "Nomatch",
        "Synthetic",
        "1901-01-01",
        "unknown",
        UnknownSubscriberId);

    private static JsonObject Build(
        string requestingPayerId, string given, string family, string birthDate, string gender, string subscriberId)
    {
        if (string.IsNullOrEmpty(requestingPayerId))
        {
            throw new InteropEnvironmentException(
                "interop/cho-seed.json has no payerBackendClient.payerId, so there is no requesting payer for the " +
                "member-match consent to name.");
        }

        var patient = new JsonObject
        {
            ["resourceType"] = "Patient",
            ["id"] = "1",
            ["meta"] = new JsonObject
            {
                ["profile"] = new JsonArray("http://hl7.org/fhir/us/davinci-hrex/StructureDefinition/hrex-patient-demographics"),
            },
            ["name"] = new JsonArray(new JsonObject
            {
                ["use"] = "official",
                ["family"] = family,
                ["given"] = new JsonArray(given),
            }),
            ["gender"] = gender,
            ["birthDate"] = birthDate,
        };

        var coverage = new JsonObject
        {
            ["resourceType"] = "Coverage",
            ["id"] = "cov-to-match",
            ["meta"] = new JsonObject
            {
                ["profile"] = new JsonArray("http://hl7.org/fhir/us/davinci-hrex/StructureDefinition/hrex-coverage"),
            },
            ["status"] = "active",
            ["subscriberId"] = subscriberId,
            ["beneficiary"] = new JsonObject { ["reference"] = "Patient/1" },
            ["period"] = new JsonObject { ["start"] = Start },
            ["payor"] = new JsonArray(new JsonObject
            {
                ["identifier"] = new JsonObject { ["value"] = ChoPayerId },
                ["display"] = "Cloud Health Office (interop)",
            }),
        };

        var consent = new JsonObject
        {
            ["resourceType"] = "Consent",
            ["meta"] = new JsonObject
            {
                ["profile"] = new JsonArray("http://hl7.org/fhir/us/davinci-hrex/StructureDefinition/hrex-consent"),
            },
            ["status"] = "active",
            ["scope"] = Concept("http://terminology.hl7.org/CodeSystem/consentscope", "patient-privacy"),
            ["category"] = new JsonArray(Concept("http://terminology.hl7.org/CodeSystem/v3-ActCode", "IDSCL")),
            ["patient"] = new JsonObject { ["reference"] = "Patient/1" },
            ["performer"] = new JsonArray(new JsonObject { ["reference"] = "Patient/1" }),
            ["policy"] = new JsonArray(new JsonObject
            {
                ["uri"] = "http://hl7.org/fhir/us/davinci-hrex/StructureDefinition-hrex-consent.html#sensitive",
            }),
            ["provision"] = new JsonObject
            {
                ["type"] = "permit",
                ["period"] = new JsonObject { ["start"] = Start },
                ["actor"] = new JsonArray(
                    Actor("PERF", ChoPayerId, "Cloud Health Office (interop)"),
                    Actor("IRCP", requestingPayerId, "Inferno PDex test kit")),
                ["action"] = new JsonArray(Concept("http://terminology.hl7.org/CodeSystem/consentaction", "disclose")),
            },
        };

        return new JsonObject
        {
            ["resourceType"] = "Parameters",
            ["meta"] = new JsonObject
            {
                ["profile"] = new JsonArray("http://hl7.org/fhir/us/davinci-hrex/StructureDefinition/hrex-parameters-member-match-in"),
            },
            ["parameter"] = new JsonArray(
                new JsonObject { ["name"] = "MemberPatient", ["resource"] = patient },
                new JsonObject { ["name"] = "CoverageToMatch", ["resource"] = coverage },
                new JsonObject { ["name"] = "Consent", ["resource"] = consent }),
        };
    }

    private static JsonObject Concept(string system, string code) =>
        new() { ["coding"] = new JsonArray(new JsonObject { ["system"] = system, ["code"] = code }) };

    private static JsonObject Actor(string role, string payerId, string display) => new()
    {
        ["role"] = Concept("http://terminology.hl7.org/CodeSystem/v3-ParticipationType", role),
        ["reference"] = new JsonObject
        {
            ["identifier"] = new JsonObject { ["value"] = payerId },
            ["display"] = display,
        },
    };

    private static string DatePart(string value) => value.Length >= 10 ? value[..10] : value;

    private static string Gender(string code) => code.ToUpperInvariant() switch
    {
        "F" => "female",
        "M" => "male",
        _ => "unknown",
    };
}
