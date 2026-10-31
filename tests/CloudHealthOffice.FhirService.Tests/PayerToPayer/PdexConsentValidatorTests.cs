using System.Text.Json.Nodes;
using FhirService.Services.PayerToPayer;
using FluentAssertions;

namespace CloudHealthOffice.FhirService.Tests.PayerToPayer;

/// <summary>
/// The requesting payer's Payer-to-Payer consent (HRex hrex-consent, ADR 022):
/// what CHO accepts and every refusal, one rule at a time.
/// </summary>
public class PdexConsentValidatorTests
{
    private const string Requesting = "NEW-PLAN";
    private const string Local = "THIS-PLAN";
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private static JsonObject Valid(DateTime? start = null, DateTime? end = null) =>
        PdexConsentBuilder.Build("m-1", Requesting, Local, (start ?? Now.AddDays(-1)) + PdexConsentValidator.StartSkew, end,
            "Lee", "1980-02-03");

    private static PdexConsentValidation Check(JsonNode? consent, string requesting = Requesting) =>
        PdexConsentValidator.Validate(consent, requesting, Local, Now);

    private static JsonObject With(Action<JsonObject> change)
    {
        var consent = Valid();
        change(consent);
        return consent;
    }

    /// <summary>
    /// The consent CHO sends as the new payer matches hrex-consent's slices: the
    /// prior payer is the source actor (provenance performer, not v3 PERF), the
    /// member is the performer, and the authorizing registry consent is the
    /// source document. It still passes CHO's own check.
    /// </summary>
    [Fact]
    public void The_outbound_consent_follows_hrex_consent_and_is_still_accepted()
    {
        var consent = PdexConsentBuilder.Build("m-1", Requesting, Local, Now.AddDays(-1) + PdexConsentValidator.StartSkew,
            null, "Lee", "1980-02-03", sourceConsentId: "consent-42");

        var actors = consent["provision"]!["actor"]!.AsArray()
            .Select(a => (a!["role"]!["coding"]![0]!["system"]!.GetValue<string>(),
                          a["role"]!["coding"]![0]!["code"]!.GetValue<string>(),
                          a["reference"]!["identifier"]!["value"]!.GetValue<string>()))
            .ToList();
        actors.Should().Equal(
            ("http://terminology.hl7.org/CodeSystem/provenance-participant-type", "performer", Local),
            ("http://terminology.hl7.org/CodeSystem/v3-ParticipationType", "IRCP", Requesting));
        consent["performer"]![0]!["reference"]!.GetValue<string>().Should().Be("#member");
        consent["sourceReference"]!["identifier"]!["value"]!.GetValue<string>().Should().Be("consent-42");
        consent["sourceReference"]!["type"]!.GetValue<string>().Should().Be("DocumentReference");

        Check(consent).Valid.Should().BeTrue();
        PdexConsentBuilder.Build("m-1", Requesting, Local, Now, null).ContainsKey("sourceReference")
            .Should().BeFalse("no registry consent id, no source to name");
    }

    [Fact]
    public void A_consent_naming_the_requesting_payer_is_accepted_with_its_period_and_a_fingerprint()
    {
        var end = Now.AddYears(1);
        var result = Check(Valid(end: end));

        result.Valid.Should().BeTrue();
        result.Policy.Should().Be(PdexConsentValidator.SensitivePolicy);
        result.PeriodStartUtc.Should().Be(Now.AddDays(-1));
        result.PatientIdentifiers.Should().Equal("M1");
        result.PeriodEndUtc.Should().Be(end);
        result.Fingerprint.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void The_payer_comparison_ignores_case_spaces_and_hyphens()
        => Check(Valid(), requesting: "new plan").Valid.Should().BeTrue();

    [Fact]
    public void A_contained_organization_can_name_a_party()
    {
        var consent = With(c =>
        {
            c["contained"]!.AsArray().Add(new JsonObject
            {
                ["resourceType"] = "Organization", ["id"] = "new",
                ["identifier"] = new JsonArray(new JsonObject { ["value"] = Requesting }),
            });
            c["provision"]!["actor"]![1]!["reference"] = new JsonObject { ["reference"] = "#new" };
        });

        Check(consent).Valid.Should().BeTrue();
    }

    public static TheoryData<string, PdexConsentFailure> Refusals => new()
    {
        { "missing", PdexConsentFailure.Missing },
        { "patient-resource", PdexConsentFailure.NotAConsent },
        { "draft", PdexConsentFailure.NotActive },
        { "scope", PdexConsentFailure.WrongScope },
        { "category", PdexConsentFailure.WrongCategory },
        { "no-patient", PdexConsentFailure.NoPatient },
        { "no-policy", PdexConsentFailure.NoPolicy },
        { "regular", PdexConsentFailure.SegmentationUnsupported },
        { "deny", PdexConsentFailure.NotPermit },
        { "no-period", PdexConsentFailure.NotInForce },
        { "bad-start", PdexConsentFailure.NotInForce },
        { "future", PdexConsentFailure.NotInForce },
        { "nested-provision", PdexConsentFailure.SegmentationUnsupported },
        { "class", PdexConsentFailure.SegmentationUnsupported },
        { "security-label", PdexConsentFailure.SegmentationUnsupported },
        { "both-policies", PdexConsentFailure.SegmentationUnsupported },
        { "duplicate-key", PdexConsentFailure.NotAConsent },
        { "us-date", PdexConsentFailure.NotInForce },
        { "dateTime-without-zone", PdexConsentFailure.NotInForce },
        { "wrong-code-system", PdexConsentFailure.WrongCategory },
        { "ended", PdexConsentFailure.NotInForce },
        { "ends-now", PdexConsentFailure.NotInForce },
        { "other-recipient", PdexConsentFailure.UnknownParty },
        { "recipient-not-ircp", PdexConsentFailure.RecipientNotBound },
        { "no-actors", PdexConsentFailure.RecipientNotBound },
        { "actor-without-identifier", PdexConsentFailure.UnknownParty },
        { "access-only", PdexConsentFailure.ActionNotDisclose },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Each_rule_refuses_on_its_own(string kind, PdexConsentFailure expected)
    {
        JsonNode? consent = kind switch
        {
            "missing" => null,
            "patient-resource" => new JsonObject { ["resourceType"] = "Patient" },
            "draft" => With(c => c["status"] = "draft"),
            "scope" => With(c => c["scope"] = Concept("research")),
            "category" => With(c => c["category"] = new JsonArray(Concept("INFA"))),
            "no-patient" => With(c => c.Remove("patient")),
            "no-policy" => With(c => c.Remove("policy")),
            "regular" => With(c => c["policy"] = new JsonArray(new JsonObject { ["uri"] = PdexConsentValidator.RegularPolicy })),
            "deny" => With(c => c["provision"]!["type"] = "deny"),
            "no-period" => With(c => c["provision"]!.AsObject().Remove("period")),
            "bad-start" => With(c => c["provision"]!["period"]!["start"] = "soon"),
            "future" => Valid(start: Now.AddMinutes(10)),
            "nested-provision" => With(c => c["provision"]!["provision"] = new JsonArray(new JsonObject { ["type"] = "deny" })),
            "class" => With(c => c["provision"]!["class"] = new JsonArray(new JsonObject { ["code"] = "Observation" })),
            "security-label" => With(c => c["provision"]!["securityLabel"] = new JsonArray(new JsonObject { ["code"] = "R" })),
            "both-policies" => With(c => c["policy"]!.AsArray().Add(new JsonObject { ["uri"] = PdexConsentValidator.RegularPolicy })),
            "duplicate-key" => JsonNode.Parse(Valid().ToJsonString().Replace("\"status\":\"active\"", "\"status\":\"active\",\"status\":\"active\"")),
            "us-date" => With(c => c["provision"]!["period"]!["start"] = "10/19/2026"),
            "dateTime-without-zone" => With(c => c["provision"]!["period"]!["start"] = "2026-10-19T10:00:00"),
            "wrong-code-system" => With(c => c["category"] = new JsonArray(new JsonObject
            {
                ["coding"] = new JsonArray(new JsonObject { ["system"] = "http://example.org/codes", ["code"] = "IDSCL" }),
            })),
            "ended" => Valid(end: Now.AddDays(-1)),
            "ends-now" => Valid(end: Now),
            "other-recipient" => PdexConsentBuilder.Build("Patient/m-1", "SOMEONE-ELSE", Local, Now.AddDays(-1), null),
            "recipient-not-ircp" => With(c => c["provision"]!["actor"]![1]!["role"] = Concept("PERF")),
            "no-actors" => With(c => c["provision"]!.AsObject().Remove("actor")),
            "actor-without-identifier" => With(c => c["provision"]!["actor"]![0]!["reference"] = new JsonObject { ["display"] = "x" }),
            "access-only" => With(c => c["provision"]!["action"] = new JsonArray(Concept("access"))),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var result = Check(consent);

        result.Valid.Should().BeFalse();
        result.Failure.Should().Be(expected);
    }

    [Fact]
    public void A_requesting_payer_that_cannot_be_named_is_never_bound()
        => Check(Valid(), requesting: " ").Failure.Should().Be(PdexConsentFailure.RecipientNotBound);

    [Fact]
    public void The_consent_names_a_member_by_identifier_or_by_name_and_birth_date()
    {
        var result = Check(Valid());

        result.NamesMember(["M-1"], null, null).Should().BeTrue("identifiers compare normalized");
        result.NamesMember(["other", "SUB 9"], "LEE", "1980-02-03").Should().BeTrue("family and birth date agree");
        result.NamesMember(["other"], "Lee", "1980-02-04").Should().BeFalse();
        result.NamesMember(["other"], "Kim", "1980-02-03").Should().BeFalse();
    }

    [Fact]
    public void A_patient_that_resolves_to_no_identifier_or_contained_patient_binds_to_no_member()
    {
        var result = Check(With(c => c["patient"] = new JsonObject { ["reference"] = "Patient/m-1" }));

        result.Valid.Should().BeTrue();
        result.NamesMember(["m-1", "Patient/m-1"], "Lee", "1980-02-03").Should().BeFalse(
            "a bare reference is not resolved against CHO's records");
    }

    [Fact]
    public void A_start_a_few_minutes_ahead_is_tolerated_as_clock_skew()
    {
        Check(PdexConsentBuilder.Build("m-1", Requesting, Local, Now.AddMinutes(3) + PdexConsentValidator.StartSkew, null))
            .Valid.Should().BeTrue();
    }

    [Fact]
    public void A_date_only_end_runs_through_that_day_and_a_year_only_start_begins_it()
    {
        var consent = With(c => c["provision"]!["period"] = new JsonObject { ["start"] = "2026", ["end"] = "2026-10-20" });

        var result = Check(consent);

        result.Valid.Should().BeTrue("the end day is included");
        result.PeriodStartUtc.Should().Be(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        result.PeriodEndUtc.Should().Be(new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void A_date_only_period_is_read_as_midnight_utc()
    {
        var consent = With(c => c["provision"]!["period"] = new JsonObject { ["start"] = "2026-10-20", ["end"] = "2027-10-20" });

        var result = Check(consent);

        result.Valid.Should().BeTrue();
        result.PeriodStartUtc.Should().Be(new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc));
    }

    private static JsonObject Concept(string code) =>
        new() { ["coding"] = new JsonArray(new JsonObject { ["code"] = code }) };
}
