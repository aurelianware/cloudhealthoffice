using ClaimsService.Fhir;
using ClaimsService.Models;
using FluentAssertions;

namespace CloudHealthOffice.ClaimsService.Tests.Fhir;

public class ClaimProcedureCodingTests
{
    [Theory]
    [InlineData("99214", "http://www.ama-assn.org/go/cpt")]
    [InlineData("0012U", "http://www.ama-assn.org/go/cpt")]
    [InlineData("J1745", "https://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets")]
    [InlineData("A0428", "https://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets")]
    [InlineData("D2740", "http://www.ada.org/cdt")]
    [InlineData("LOCAL-CODE", "urn:cho:procedure-code")]
    public void EveryClaimProjection_UsesTheProcedureVocabularyAndPreservesFreeText(string code, string system)
    {
        var claim = new Claim
        {
            Id = "claim-1", ClaimNumber = "synthetic", MemberId = "member-1",
            BillingProviderNPI = "1234567893", ClaimType = ClaimType.Professional,
            ClaimLines = [new ClaimLine
            {
                LineNumber = 1, ProcedureCode = code, ProcedureDescription = "Plan supplied description",
                ChargeAmount = 100m, Units = 1,
            }],
        };

        var projections = new[]
        {
            new ClaimFhirProjector().Project(claim),
            new ExplanationOfBenefitProjector().Project(claim),
            new CarinNonFinancialEobProjector().Project(claim),
        };
        foreach (var projection in projections)
        {
            var concept = projection["item"]!.AsArray()[0]!["productOrService"]!;
            concept["coding"]!.AsArray()[0]!["system"]!.GetValue<string>().Should().Be(system);
            concept["coding"]!.AsArray()[0]!["code"]!.GetValue<string>().Should().Be(code);
            concept["coding"]!.AsArray()[0]!.AsObject().ContainsKey("display").Should().BeFalse();
            concept["text"]!.GetValue<string>().Should().Be("Plan supplied description");
        }
    }

    [Fact]
    public void A_CARIN_EOB_line_with_only_a_local_code_also_says_not_applicable()
    {
        // An inpatient room-and-board line: a revenue code and no procedure.
        // CARIN BB's productOrService value sets are required bindings that
        // hold data-absent-reason#not-applicable for this case (run 40979709678).
        var claim = new Claim
        {
            Id = "claim-1", ClaimNumber = "synthetic", MemberId = "member-1",
            BillingProviderNPI = "1234567893", ClaimType = ClaimType.Institutional,
            ClaimLines = [new ClaimLine
            {
                LineNumber = 1, ProcedureCode = "0120", RevenueCode = "0120",
                ChargeAmount = 100m, Units = 1,
            }],
        };

        foreach (var projection in new[]
                 {
                     new ExplanationOfBenefitProjector().Project(claim),
                     new CarinNonFinancialEobProjector().Project(claim),
                 })
        {
            var codings = projection["item"]!.AsArray()[0]!["productOrService"]!["coding"]!.AsArray();
            codings.Select(c => (c!["system"]!.GetValue<string>(), c["code"]!.GetValue<string>())).Should().Equal(
                ("urn:cho:procedure-code", "0120"),
                ("http://terminology.hl7.org/CodeSystem/data-absent-reason", "not-applicable"));
        }

        new ClaimFhirProjector().Project(claim)["item"]!.AsArray()[0]!["productOrService"]!["coding"]!.AsArray()
            .Should().ContainSingle("R4 Claim has no such binding, so the Claim projection is unchanged");
    }

    [Fact]
    public void A_CARIN_EOB_line_with_a_standard_code_has_only_that_code()
    {
        var claim = new Claim
        {
            Id = "claim-1", ClaimNumber = "synthetic", MemberId = "member-1",
            BillingProviderNPI = "1234567893", ClaimType = ClaimType.Professional,
            ClaimLines = [new ClaimLine { LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 100m, Units = 1 }],
        };

        new CarinNonFinancialEobProjector().Project(claim)["item"]!.AsArray()[0]!["productOrService"]!["coding"]!.AsArray()
            .Should().ContainSingle();
    }
}
