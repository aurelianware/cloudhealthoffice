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
}
