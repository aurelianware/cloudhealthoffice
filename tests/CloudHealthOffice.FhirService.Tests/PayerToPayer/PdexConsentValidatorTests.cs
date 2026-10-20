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
        PdexConsentBuilder.Build("Patient/m-1", Requesting, Local, start ?? Now.AddDays(-1), end);

    private static PdexConsentValidation Check(JsonNode? consent, string requesting = Requesting) =>
        PdexConsentValidator.Validate(consent, requesting, Local, Now);

    private static JsonObject With(Action<JsonObject> change)
    {
        var consent = Valid();
        change(consent);
        return consent;
    }

    [Fact]
    public void A_consent_naming_the_requesting_payer_is_accepted_with_its_period_and_a_fingerprint()
    {
        var end = Now.AddYears(1);
        var result = Check(Valid(end: end));

        result.Valid.Should().BeTrue();
        result.Policy.Should().Be(PdexConsentValidator.SensitivePolicy);
        result.PeriodStartUtc.Should().Be(Now.AddDays(-1));
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
            c["contained"] = new JsonArray(new JsonObject
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
            "future" => Valid(start: Now.AddMinutes(1)),
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
