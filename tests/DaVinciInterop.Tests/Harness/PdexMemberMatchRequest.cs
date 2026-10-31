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
/// gender) and the new payer's own member id, typed HRex <c>UMB</c>: US Core
/// Patient, which HRex 1.0.0 requires of <c>MemberPatient</c>, needs an
/// identifier, and a new payer does not know CHO's. <c>CoverageToLink</c> is the
/// member's coverage with the new payer, which the kit's must-support tests
/// expect.
/// </para>
///
/// <para>
/// The Consent is HRex 1.0.0's <c>hrex-consent</c>: patient-privacy scope,
/// IDSCL category, the <c>#sensitive</c> policy, a source document reference,
/// a bounded permit period, a permit from CHO's payer (source, provenance
/// <c>performer</c>) to the kit's registered payer (information recipient,
/// <c>IRCP</c>), and the member named by a local
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

    /// <summary>When the consent ends (HRex requires an end); after any run of this harness's pins.</summary>
    public const string End = "2027-12-31";

    /// <summary>The member id the requesting payer (the kit) has for the member.</summary>
    public const string NewPayerMemberId = "INFERNO-NEW-PAYER-MEMBER-1";

    private const string NewPayerMemberSystem = "urn:cho:interop:inferno-new-payer:member-id";

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
            ["identifier"] = new JsonArray(new JsonObject
            {
                // Not MB: CHO reads a MemberPatient MB identifier as its own
                // member id. UMB is HRex's type for the new payer's id.
                ["type"] = Concept("http://hl7.org/fhir/us/davinci-hrex/CodeSystem/hrex-temp", "UMB"),
                ["system"] = NewPayerMemberSystem,
                ["value"] = NewPayerMemberId,
            }),
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

        var coverageToLink = new JsonObject
        {
            ["resourceType"] = "Coverage",
            ["id"] = "cov-to-link",
            ["meta"] = new JsonObject
            {
                ["profile"] = new JsonArray("http://hl7.org/fhir/us/davinci-hrex/StructureDefinition/hrex-coverage"),
            },
            ["status"] = "active",
            ["identifier"] = new JsonArray(new JsonObject
            {
                ["system"] = NewPayerMemberSystem,
                ["value"] = NewPayerMemberId,
            }),
            ["subscriberId"] = NewPayerMemberId,
            ["beneficiary"] = new JsonObject { ["reference"] = "Patient/1" },
            // No period: HRex asks for only must-support elements here, and the
            // kit's minimal-data test fails a CoverageToLink that carries one.
            ["payor"] = new JsonArray(new JsonObject
            {
                ["identifier"] = new JsonObject { ["value"] = requestingPayerId },
                ["display"] = "Inferno PDex test kit",
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
            ["sourceReference"] = new JsonObject
            {
                ["reference"] = "urn:cho:interop:inferno-new-payer:consent-document-1",
                ["display"] = "The member's signed Payer-to-Payer consent (synthetic)",
            },
            ["policy"] = new JsonArray(new JsonObject
            {
                ["uri"] = "http://hl7.org/fhir/us/davinci-hrex/StructureDefinition-hrex-consent.html#sensitive",
            }),
            ["provision"] = new JsonObject
            {
                ["type"] = "permit",
                ["period"] = new JsonObject { ["start"] = Start, ["end"] = End },
                ["actor"] = new JsonArray(
                    Actor("http://terminology.hl7.org/CodeSystem/provenance-participant-type", "performer",
                        ChoPayerId, "Cloud Health Office (interop)"),
                    Actor("http://terminology.hl7.org/CodeSystem/v3-ParticipationType", "IRCP",
                        requestingPayerId, "Inferno PDex test kit")),
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
                new JsonObject { ["name"] = "CoverageToLink", ["resource"] = coverageToLink },
                new JsonObject { ["name"] = "Consent", ["resource"] = consent }),
        };
    }

    private static JsonObject Concept(string system, string code) =>
        new() { ["coding"] = new JsonArray(new JsonObject { ["system"] = system, ["code"] = code }) };

    private static JsonObject Actor(string roleSystem, string role, string payerId, string display) => new()
    {
        ["role"] = Concept(roleSystem, role),
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
