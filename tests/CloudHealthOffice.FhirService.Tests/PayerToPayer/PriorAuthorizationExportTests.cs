using System.Text.Json.Nodes;
using FhirService.Services.PayerToPayer;
using FluentAssertions;

namespace CloudHealthOffice.FhirService.Tests.PayerToPayer;

/// <summary>
/// Prior-authorization data in a Payer-to-Payer export (gap matrix G-35).
/// CMS-0057-F asks the prior payer for the authorizations that still matter to
/// the member — the active and pending ones, and the recently decided — so that
/// a receiving payer can honour what is already approved instead of making the
/// member start again.
///
/// Two things are pinned here: which authorizations leave, and what of each one
/// leaves. The second has its own allowlist, because what a receiving payer
/// needs from an authorization (the decision, the approved period, the reason
/// for a refusal) is what the CLAIM allowlist deliberately keeps home.
/// </summary>
public class PriorAuthorizationExportTests
{
    private static readonly DateTime Exchange = new(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc);

    private static JsonObject PriorAuth(
        string outcome = "complete",
        string? benefitStart = null,
        string? benefitEnd = null,
        string? created = null)
    {
        var eob = new JsonObject
        {
            ["resourceType"] = "ExplanationOfBenefit",
            ["id"] = "pa-PA-1",
            ["use"] = "preauthorization",
            ["outcome"] = outcome,
            ["patient"] = new JsonObject { ["reference"] = "Patient/M1" },
        };
        if (created is not null) eob["created"] = created;
        if (benefitStart is not null || benefitEnd is not null)
        {
            var period = new JsonObject();
            if (benefitStart is not null) period["start"] = benefitStart;
            if (benefitEnd is not null) period["end"] = benefitEnd;
            eob["benefitPeriod"] = period;
        }
        return eob;
    }

    // ── which authorizations leave ───────────────────────────────────────────

    [Fact]
    public void AnOpenRequestIsExportedWhateverItsAge()
    {
        // A receiving payer inherits an open request, so age is irrelevant: the
        // member is still waiting for this decision.
        var ancient = PriorAuth(outcome: "queued", created: "2015-01-01");

        PayerToPayerExportPolicy.IncludePriorAuthorization(ancient, Exchange).Should().BeTrue();
    }

    [Fact]
    public void ARecentlyEndedApprovalIsExported()
    {
        var recent = PriorAuth(benefitStart: "2026-08-01", benefitEnd: "2026-09-30");

        PayerToPayerExportPolicy.IncludePriorAuthorization(recent, Exchange).Should().BeTrue();
    }

    [Fact]
    public void AnApprovalStillInForceIsExported()
    {
        var current = PriorAuth(benefitStart: "2026-10-01", benefitEnd: "2026-12-31");

        PayerToPayerExportPolicy.IncludePriorAuthorization(current, Exchange).Should().BeTrue();
    }

    [Fact]
    public void AnApprovalThatEndedLongAgoIsNotExported()
    {
        // Past the one-year window, which matches the prior-auth retention
        // anchor: nothing is offered here that CHO would already have purged.
        var stale = PriorAuth(benefitStart: "2020-01-01", benefitEnd: "2020-06-30");

        PayerToPayerExportPolicy.IncludePriorAuthorization(stale, Exchange).Should().BeFalse();
    }

    [Fact]
    public void WithNoApprovedEndDate_TheStartDateDecides()
    {
        PayerToPayerExportPolicy
            .IncludePriorAuthorization(PriorAuth(benefitStart: "2026-10-01"), Exchange)
            .Should().BeTrue();
        PayerToPayerExportPolicy
            .IncludePriorAuthorization(PriorAuth(benefitStart: "2019-10-01"), Exchange)
            .Should().BeFalse();
    }

    [Fact]
    public void WithNoApprovedPeriodAtAll_TheRequestDateDecides()
    {
        // A denial has no approved period; it is still recent history the
        // receiving payer can use.
        PayerToPayerExportPolicy
            .IncludePriorAuthorization(PriorAuth(created: "2026-09-15"), Exchange)
            .Should().BeTrue();
        PayerToPayerExportPolicy
            .IncludePriorAuthorization(PriorAuth(created: "2018-09-15"), Exchange)
            .Should().BeFalse();
    }

    [Fact]
    public void ADecidedAuthorizationWithNoReadableDateIsNotExported()
    {
        // The same conservative reading a payment with no parseable date gets:
        // a record whose window cannot be established is not assumed to be
        // inside it.
        PayerToPayerExportPolicy
            .IncludePriorAuthorization(PriorAuth(), Exchange)
            .Should().BeFalse();
        PayerToPayerExportPolicy
            .IncludePriorAuthorization(PriorAuth(created: "not a date"), Exchange)
            .Should().BeFalse();
    }

    // ── what of each one leaves ──────────────────────────────────────────────

    [Fact]
    public void TheDecisionAndTheApprovedPeriodSurviveRedaction()
    {
        var eob = PriorAuth(benefitStart: "2026-11-01", benefitEnd: "2026-11-20");
        eob["disposition"] = "Approved in part";
        eob["subType"] = new JsonObject { ["coding"] = new JsonArray() };
        eob["processNote"] = new JsonArray
        {
            new JsonObject { ["number"] = 1, ["text"] = "UNITSRED: approved for 12 of 24 visits." },
        };
        eob["insurance"] = new JsonArray
        {
            new JsonObject { ["focal"] = true, ["preAuthRef"] = new JsonArray { "PA-1" } },
        };

        var redacted = PayerToPayerEobRedaction.RedactPriorAuthorization(eob);

        // The whole point of sharing a prior authorization.
        redacted["outcome"]!.GetValue<string>().Should().Be("complete");
        redacted["disposition"]!.GetValue<string>().Should().Be("Approved in part");
        redacted["benefitPeriod"]!["end"]!.GetValue<string>().Should().Be("2026-11-20");
        redacted["use"]!.GetValue<string>().Should().Be("preauthorization");
        redacted["subType"].Should().NotBeNull();
        redacted["processNote"]!.AsArray().Should().HaveCount(1);
        redacted["insurance"]!.AsArray()[0]!["preAuthRef"]!.AsArray()[0]!.GetValue<string>()
            .Should().Be("PA-1");
    }

    [Fact]
    public void ItemUnitsAndTheDenialReasonSurvive()
    {
        var eob = PriorAuth(created: "2026-10-01");
        eob["item"] = new JsonArray
        {
            new JsonObject
            {
                ["sequence"] = 1,
                ["productOrService"] = new JsonObject { ["coding"] = new JsonArray() },
                ["quantity"] = new JsonObject { ["value"] = 24 },
                ["adjudication"] = new JsonArray
                {
                    new JsonObject { ["category"] = new JsonObject { ["text"] = "approved-units" } },
                },
            },
        };

        var item = PayerToPayerEobRedaction.RedactPriorAuthorization(eob)["item"]!.AsArray()[0]!;

        item["quantity"]!["value"]!.GetValue<int>().Should().Be(24);
        item["adjudication"]!.AsArray().Should().HaveCount(1);
    }

    [Fact]
    public void AnythingNotOnTheAllowlistStaysHome()
    {
        // An allowlist, so an element a later projector adds does not leave
        // until someone lists it here deliberately.
        var eob = PriorAuth(created: "2026-10-01");
        eob["total"] = new JsonArray { new JsonObject { ["amount"] = new JsonObject { ["value"] = 1234 } } };
        eob["payment"] = new JsonObject { ["amount"] = new JsonObject { ["value"] = 1234 } };
        eob["payee"] = new JsonObject { ["type"] = new JsonObject() };
        eob["supportingInfo"] = new JsonArray { new JsonObject { ["sequence"] = 1 } };
        eob["contained"] = new JsonArray { new JsonObject { ["resourceType"] = "Practitioner" } };
        eob["text"] = new JsonObject { ["div"] = "<div>narrative</div>" };
        eob["somethingInvented"] = "later addition";

        var redacted = PayerToPayerEobRedaction.RedactPriorAuthorization(eob);

        redacted.Should().NotContainKey("total");
        redacted.Should().NotContainKey("payment");
        redacted.Should().NotContainKey("payee");
        redacted.Should().NotContainKey("supportingInfo");
        redacted.Should().NotContainKey("contained");
        redacted.Should().NotContainKey("text");
        redacted.Should().NotContainKey("somethingInvented");
    }

    [Fact]
    public void TheClaimAllowlistIsNotWidenedByTheNewOne()
    {
        // The two allowlists are not interchangeable: a claim's disposition,
        // process notes, item adjudication and amounts are CHO's internal
        // adjudication and money, and must still stay home.
        var claim = new JsonObject
        {
            ["resourceType"] = "ExplanationOfBenefit",
            ["id"] = "CHAIN-1",
            ["use"] = "claim",
            ["disposition"] = "Paid",
            ["processNote"] = new JsonArray { new JsonObject { ["text"] = "internal" } },
            ["benefitPeriod"] = new JsonObject { ["start"] = "2026-01-01" },
            ["payment"] = new JsonObject { ["amount"] = new JsonObject { ["value"] = 100 } },
            ["item"] = new JsonArray
            {
                new JsonObject
                {
                    ["sequence"] = 1,
                    ["adjudication"] = new JsonArray { new JsonObject { ["category"] = new JsonObject() } },
                },
            },
        };

        var redacted = PayerToPayerEobRedaction.Redact(claim);

        redacted.Should().NotContainKey("disposition");
        redacted.Should().NotContainKey("processNote");
        redacted.Should().NotContainKey("benefitPeriod");
        redacted.Should().NotContainKey("payment");
        redacted["item"]!.AsArray()[0]!.AsObject().Should().NotContainKey("adjudication");
    }

    [Fact]
    public void TheOriginalIsNotMutated()
    {
        var eob = PriorAuth(created: "2026-10-01");
        eob["payment"] = new JsonObject { ["amount"] = new JsonObject { ["value"] = 1234 } };

        PayerToPayerEobRedaction.RedactPriorAuthorization(eob);

        eob.Should().ContainKey("payment", "redaction returns a copy and leaves its input alone");
    }
}
