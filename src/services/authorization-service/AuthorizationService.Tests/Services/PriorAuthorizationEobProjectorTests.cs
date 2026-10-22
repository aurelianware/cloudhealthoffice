using System.Text.Json.Nodes;
using AuthorizationService.Models;
using AuthorizationService.Services.Fhir;
using FluentAssertions;

namespace AuthorizationService.Tests.Services;

/// <summary>
/// The projection CMS-0057-F's PA-data requirement rests on. These pin the
/// facts the rule names — status, dates, items, quantity, denial reason — and
/// the discriminator (<c>use = preauthorization</c>) a PDex consumer searches
/// on. A regression in any of them is a compliance regression, not a cosmetic
/// one.
/// </summary>
public class PriorAuthorizationEobProjectorTests
{
    private readonly PriorAuthorizationEobProjector _projector = new();

    private static Authorization Decided() => new()
    {
        TenantId = "tenant-1",
        MemberId = "M1",
        AuthorizationNumber = "PA-20261022-ABCD1234",
        CoverageId = "COV1",
        PatientFirstName = "Ada",
        PatientLastName = "Lovelace",
        LineOfBusiness = LineOfBusiness.Medicaid,
        RequestingProviderNPI = "1234567893",
        RequestingProviderName = "Dr Requesting",
        ServicingProviderNPI = "1987654321",
        ServicingProviderName = "Servicing Clinic",
        ServiceTypeCode = "2",
        AuthorizationType = AuthorizationType.PreAuthorization,
        RequestedServiceDateFrom = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
        RequestedServiceDateTo = new DateTime(2026, 11, 30, 0, 0, 0, DateTimeKind.Utc),
        ApprovedServiceDateFrom = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
        ApprovedServiceDateTo = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc),
        Status = AuthorizationStatus.Modified,
        SubmittedDate = new DateTime(2026, 10, 20, 14, 30, 0, DateTimeKind.Utc),
        LastUpdatedDate = new DateTime(2026, 10, 21, 9, 0, 0, DateTimeKind.Utc),
        DiagnosisCodes =
        [
            new DiagnosisCode { Code = "M5451", Description = "Vertebrogenic low back pain" },
        ],
        RequestedServices =
        [
            new RequestedService
            {
                LineNumber = 1,
                ProcedureCode = "97110",
                ProductOrServiceSystem = "cpt",
                ProcedureDescription = "Therapeutic exercises",
                Modifiers = ["GP"],
                RequestedUnits = 24,
                ApprovedUnits = 12,
                UnitType = "visits",
                DenialReasonCode = "UNITSRED",
                DenialReason = "Approved for 12 of 24 requested visits.",
            },
        ],
    };

    [Fact]
    public void TheResourceIsAPriorAuthorization_NotAClaim()
    {
        var eob = _projector.Project(Decided());

        eob["resourceType"]!.GetValue<string>().Should().Be("ExplanationOfBenefit");
        // The discriminator a PDex consumer searches on, and what makes
        // fhir-service route a read here rather than to claims-service.
        eob["use"]!.GetValue<string>().Should().Be("preauthorization");
        eob["meta"]!["profile"]!.AsArray().Select(p => p!.GetValue<string>())
            .Should().Contain(PriorAuthorizationEobProjector.PdexPriorAuthorizationProfile);
    }

    [Fact]
    public void TheResourceIdCarriesThePrefix_AndRoundTripsBackToTheNumber()
    {
        var authorization = Decided();

        var id = _projector.ResourceId(authorization);

        id.Should().Be("pa-PA-20261022-ABCD1234");
        PriorAuthorizationEobProjector.AuthorizationNumberFromResourceId(id)
            .Should().Be(authorization.AuthorizationNumber);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("claim-version-123")]
    // The prefix alone names no authorization.
    [InlineData("pa-")]
    public void AnIdThatIsNotOurs_DoesNotResolve(string id)
    {
        PriorAuthorizationEobProjector.AuthorizationNumberFromResourceId(id).Should().BeNull();
    }

    [Fact]
    public void ThePatientAndTheAuthorizationNumberAreBothPresent()
    {
        var eob = _projector.Project(Decided());

        eob["patient"]!["reference"]!.GetValue<string>().Should().Be("Patient/M1");
        eob["identifier"]!.AsArray()[0]!["value"]!.GetValue<string>()
            .Should().Be("PA-20261022-ABCD1234");
        // preAuthRef is the number a provider quotes on the claim.
        eob["insurance"]!.AsArray()[0]!["preAuthRef"]!.AsArray()[0]!.GetValue<string>()
            .Should().Be("PA-20261022-ABCD1234");
        eob["insurance"]!.AsArray()[0]!["coverage"]!["reference"]!.GetValue<string>()
            .Should().Be("Coverage/COV1");
    }

    [Fact]
    public void TheRequestedAndApprovedPeriodsAreBothCarried()
    {
        var eob = _projector.Project(Decided());

        // What was asked for.
        eob["billablePeriod"]!["start"]!.GetValue<string>().Should().Be("2026-11-01");
        eob["billablePeriod"]!["end"]!.GetValue<string>().Should().Be("2026-11-30");
        // What was actually granted — the date range the member and a receiving
        // payer need.
        eob["benefitPeriod"]!["start"]!.GetValue<string>().Should().Be("2026-11-01");
        eob["benefitPeriod"]!["end"]!.GetValue<string>().Should().Be("2026-11-20");
    }

    [Fact]
    public void WithNoApprovedEndDate_TheExpirationDateIsTheEndOfTheGrant()
    {
        var authorization = Decided();
        authorization.ApprovedServiceDateTo = null;
        authorization.ExpirationDate = new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        var eob = _projector.Project(authorization);

        eob["benefitPeriod"]!["end"]!.GetValue<string>().Should().Be("2027-01-15");
    }

    [Fact]
    public void TheItemCarriesTheCodeModifiersAndBothUnitCounts()
    {
        var eob = _projector.Project(Decided());

        var item = eob["item"]!.AsArray()[0]!;
        item["sequence"]!.GetValue<int>().Should().Be(1);
        item["productOrService"]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>().Should().Be("97110");
        item["modifier"]!.AsArray()[0]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>().Should().Be("GP");
        // The quantity requested is item.quantity — PDex's closed adjudication
        // slicing has no slice for it, so it is not also an adjudication entry.
        item["quantity"]!["value"]!.GetValue<decimal>().Should().Be(24);
        item["quantity"]!["unit"]!.GetValue<string>().Should().Be("visits");

        var adjudication = item["adjudication"]!.AsArray();
        UnitValue(adjudication, PriorAuthorizationEobProjector.AllowedUnitsCode).Should().Be(12);
    }

    [Fact]
    public void EveryAdjudicationCategoryIsOneOfPdexsOwnSlices()
    {
        // PDex slices adjudication closed on category, so a CHO-private
        // category is a slice the profile rejects. Only allowedunits,
        // consumedunits and denialreason exist.
        var categories = _projector.Project(Decided())["item"]!.AsArray()
            .SelectMany(i => i!["adjudication"]?.AsArray() ?? [])
            .Select(a => (
                System: a!["category"]!["coding"]!.AsArray()[0]!["system"]!.GetValue<string>(),
                Code: Category(a!)))
            .ToList();

        categories.Should().NotBeEmpty();
        categories.Select(c => c.System).Should()
            .AllBe(PriorAuthorizationEobProjector.PdexAdjudicationDiscriminatorSystem);
        categories.Select(c => c.Code).Should()
            .BeSubsetOf(["allowedunits", "consumedunits", "denialreason"]);
    }

    [Fact]
    public void ALineDenialCarriesItsCodeAndItsText()
    {
        var eob = _projector.Project(Decided());

        var denial = eob["item"]!.AsArray()[0]!["adjudication"]!.AsArray()
            .Single(a => Category(a!) == PriorAuthorizationEobProjector.DenialReasonCode)!;

        // CHO's own 278 review code, under its own system: the profile binds
        // reason to X12 CARCs, which this record does not carry (G-17), and a
        // mapped CARC would be data CHO does not hold.
        denial["reason"]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>().Should().Be("UNITSRED");
        denial["reason"]!["text"]!.GetValue<string>()
            .Should().Be("Approved for 12 of 24 requested visits.");
    }

    [Fact]
    public void AHeaderDenialBecomesAReadableProcessNote()
    {
        var authorization = Decided();
        authorization.Status = AuthorizationStatus.Denied;
        authorization.DenialReasonCode = "NOTMEDNEC";
        authorization.DenialReason = "Clinical criteria not met.";

        var eob = _projector.Project(authorization);

        eob["outcome"]!.GetValue<string>().Should().Be("complete");
        eob["disposition"]!.GetValue<string>().Should().Be("Denied");
        eob["processNote"]!.AsArray().Select(n => n!["text"]!.GetValue<string>())
            .Should().Contain("NOTMEDNEC: Clinical criteria not met.");
    }

    [Fact]
    public void AServicingProviderThatDiffersIsSurfaced()
    {
        // FHIR allows one `provider`, so the servicing provider travels as a
        // note rather than being silently dropped.
        var eob = _projector.Project(Decided());

        eob["provider"]!["identifier"]!["value"]!.GetValue<string>().Should().Be("1234567893");
        eob["processNote"]!.AsArray().Select(n => n!["text"]!.GetValue<string>())
            .Should().Contain(t => t.Contains("1987654321", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(AuthorizationStatus.Approved, "complete", "Approved")]
    [InlineData(AuthorizationStatus.Modified, "partial", "Approved in part")]
    [InlineData(AuthorizationStatus.Denied, "complete", "Denied")]
    [InlineData(AuthorizationStatus.Expired, "complete", "Approval period has ended")]
    [InlineData(AuthorizationStatus.Cancelled, "complete", "Withdrawn")]
    // Anything still in flight is `queued`, which is how a receiving payer and a
    // member tell "no decision yet" from "decided".
    [InlineData(AuthorizationStatus.Submitted, "queued", "Received")]
    [InlineData(AuthorizationStatus.InReview, "queued", "Under review")]
    [InlineData(AuthorizationStatus.Pended, "queued", "Pended for additional information")]
    public void EveryStatusMapsToAnOutcomeAndAPlainLanguageDisposition(
        AuthorizationStatus status, string expectedOutcome, string expectedDisposition)
    {
        var authorization = Decided();
        authorization.Status = status;

        var eob = _projector.Project(authorization);

        eob["outcome"]!.GetValue<string>().Should().Be(expectedOutcome);
        eob["disposition"]!.GetValue<string>().Should().Be(expectedDisposition);
    }

    [Fact]
    public void OnlyAWithdrawnRecordIsACancelledResource()
    {
        // EOB.status is the resource's lifecycle, not the review decision: a
        // denial is an active record of a decision that was made.
        _projector.Project(With(AuthorizationStatus.Denied))["status"]!.GetValue<string>()
            .Should().Be("active");
        _projector.Project(With(AuthorizationStatus.Cancelled))["status"]!.GetValue<string>()
            .Should().Be("cancelled");

        Authorization With(AuthorizationStatus status)
        {
            var a = Decided();
            a.Status = status;
            return a;
        }
    }

    [Fact]
    public void TheServiceTypeTravelsAsSubType_AndTypeStaysHonest()
    {
        var eob = _projector.Project(Decided());

        // CHO's record carries an X12 service type, not a claim type. The
        // claim-type code system has no concept that fits — its codes are
        // institutional, oral, pharmacy, professional and vision — and the HL7
        // validator rejects the `other` this once emitted. The binding is
        // extensible, so `type` travels as text and nothing is guessed.
        eob["type"]!.AsObject().Should().NotContainKey("coding");
        eob["type"]!["text"]!.GetValue<string>().Should().Be("Prior authorization, X12 service type 2");
        eob["subType"]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>().Should().Be("2");
    }

    [Fact]
    public void ADiagnosisIsCodedAsIcd10Cm()
    {
        var eob = _projector.Project(Decided());

        var diagnosis = eob["diagnosis"]!.AsArray()[0]!;
        diagnosis["sequence"]!.GetValue<int>().Should().Be(1);
        diagnosis["diagnosisCodeableConcept"]!["coding"]!.AsArray()[0]!["system"]!.GetValue<string>()
            .Should().Be("http://hl7.org/fhir/sid/icd-10-cm");
        diagnosis["diagnosisCodeableConcept"]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>()
            .Should().Be("M5451");
    }

    [Theory]
    // An explicit hint wins.
    [InlineData("cpt", "97110", "http://www.ama-assn.org/go/cpt")]
    [InlineData("hcpcs", "G0283", "https://bluebutton.cms.gov/resources/codesystem/hcpcs")]
    // A full URI is kept verbatim.
    [InlineData("http://example.org/codes", "X1", "http://example.org/codes")]
    // No hint: shape decides, as CHO's own 278 intake reads these.
    [InlineData(null, "97110", "http://www.ama-assn.org/go/cpt")]
    [InlineData(null, "G0283", "https://bluebutton.cms.gov/resources/codesystem/hcpcs")]
    public void TheProcedureCodeSystemIsResolvedFromTheHintThenTheShape(
        string? hint, string code, string expected)
    {
        PriorAuthorizationEobProjector.ProcedureSystem(hint, code).Should().Be(expected);
    }

    [Fact]
    public void AWholeNumberOfUnitsIsNotWrittenWithTrailingZeros()
    {
        // A unit count of 1 should read as 1, not 1.00 — it is a count.
        var authorization = Decided();
        authorization.RequestedServices[0].RequestedUnits = 1m;
        authorization.RequestedServices[0].ApprovedUnits = null;

        var eob = _projector.Project(authorization);

        eob["item"]!.AsArray()[0]!["quantity"]!["value"]!.ToJsonString().Should().Be("1");
    }

    [Fact]
    public void ALineWithoutAnApprovedCountReportsOnlyWhatWasRequested()
    {
        var authorization = Decided();
        authorization.RequestedServices[0].ApprovedUnits = null;

        var item = _projector.Project(authorization)["item"]!.AsArray()[0]!;
        var adjudication = item["adjudication"]!.AsArray();

        // Nothing is approved, so there is no allowedunits slice — and the
        // requested count is still stated, on item.quantity.
        adjudication.Select(a => Category(a!)).Should()
            .NotContain(PriorAuthorizationEobProjector.AllowedUnitsCode);
        item["quantity"]!["value"]!.GetValue<decimal>().Should().Be(24);
    }

    [Fact]
    public void ALineWithNoProcedureCodeIsNotProjectedAsAnItem()
    {
        var authorization = Decided();
        authorization.RequestedServices.Add(new RequestedService { ProcedureCode = "   " });

        _projector.Project(authorization)["item"]!.AsArray().Should().HaveCount(1);
    }

    private static string? Category(JsonNode adjudication)
        => adjudication["category"]?["coding"]?.AsArray().FirstOrDefault()?["code"]?.GetValue<string>();

    private static decimal? UnitValue(JsonArray adjudication, string category)
        => adjudication.FirstOrDefault(a => Category(a!) == category)?["value"]?.GetValue<decimal>();
}
