using System.Text.Json;
using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using CloudHealthOffice.Tools.MccPlatformValidator;

namespace CloudHealthOffice.MccPlatformValidator.Tests;

public class MccTplScenariosTests
{
    private static readonly DateTime ServiceDate = new(2026, 9, 15);

    [Fact]
    public void All_CoversTexasAndCalifornia_WithBothOutcomes()
    {
        foreach (var state in new[] { "TX", "CA" })
        {
            var forState = MccTplScenarios.All.Where(s => s.State == state).ToList();
            Assert.Contains(forState, s => s.ExpectPaid);
            Assert.Contains(forState, s => !s.ExpectPaid);
        }

        Assert.Equal(
            MccTplScenarios.All.Count,
            MccTplScenarios.All.Select(MccTplScenarios.PlanIdFor).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("TxCostAvoid", false)]
    [InlineData("CaOhcCostAvoid", false)]
    [InlineData("TxPediatricPreventivePayAndChase", true)]
    [InlineData("TxCasualtyOnlyPays", true)]
    [InlineData("CaOhcCodeAPayAndChase", true)]
    [InlineData("CaOhcDentalOnlyPays", true)]
    public void AnswerKey_ScoresCostAvoidAsCarc22Denial_AndTheRestAsPaid(string name, bool expectPaid)
    {
        var scenario = MccTplScenarios.All.Single(s => s.Name == name);
        var claim = CreateClaim();
        MccTplScenarios.Apply(claim, scenario);

        var expected = MccWorkflowValidation.ExpectedValidationFor(claim);

        Assert.Equal($"Tpl:{name}", expected.Scenario);
        if (expectPaid)
        {
            Assert.Equal(ClaimValidationOutcome.Paid, expected.ExpectedOutcome);
            Assert.Null(expected.ExpectedBusinessDenialCode);
            Assert.Equal("Paid", claim.ExpectedOutcome!.Disposition);
            Assert.Equal("Matched", MccWorkflowValidation.ValidationStatus(expected, ClaimValidationOutcome.Paid, null));
        }
        else
        {
            Assert.Equal(ClaimValidationOutcome.BusinessDenial, expected.ExpectedOutcome);
            Assert.Equal(MccTplScenarios.CoveredByAnotherPayerCode, expected.ExpectedBusinessDenialCode);
            Assert.Equal("Denied", claim.ExpectedOutcome!.Disposition);
            Assert.Equal(0m, claim.ExpectedOutcome.ExpectedPaidAmount);
            // claims-service persists the bare CARC; the validator normalizes it.
            var observed = MccWorkflowValidation.NormalizeBusinessDenialCode("22");
            Assert.Equal("Matched", MccWorkflowValidation.ValidationStatus(expected, ClaimValidationOutcome.BusinessDenial, observed));
            Assert.Equal("Mismatched", MccWorkflowValidation.ValidationStatus(expected, ClaimValidationOutcome.Paid, null));
        }
    }

    [Fact]
    public void Apply_GivesEachClaimItsOwnMember_InTheScenarioState()
    {
        var scenario = MccTplScenarios.All.Single(s => s.Name == "CaOhcCostAvoid");
        var claim = CreateClaim();

        MccTplScenarios.Apply(claim, scenario);

        Assert.Equal("MBR-TPL-MCC-P-0000042", claim.Member.MemberId);
        Assert.Equal("SUB-TPL-MCC-P-0000042", claim.Member.SubscriberId);
        Assert.Equal("CA", claim.Member.State);
        Assert.True(claim.Member.IsSubscriber);
        Assert.True(claim.Member.CoverageEffectiveDate < ServiceDate);
    }

    [Fact]
    public void Apply_PediatricScenario_MakesAWellChildVisitUnder21()
    {
        var scenario = MccTplScenarios.All.Single(s => s.PediatricWellChild);
        var claim = CreateClaim();

        MccTplScenarios.Apply(claim, scenario);

        var age = ServiceDate.Year - claim.Member.DateOfBirth.Year;
        Assert.InRange(age, 8, 9);
        Assert.StartsWith("Z00.12", claim.PrimaryDiagnosisCode);
    }

    [Fact]
    public void Apply_NonPediatricScenarios_UseAdultsWithNoAccident()
    {
        foreach (var scenario in MccTplScenarios.All.Where(s => !s.PediatricWellChild))
        {
            var claim = CreateClaim(memberDateOfBirth: ServiceDate.AddYears(-5));
            claim.RelatedCausesCode = "AA";
            claim.AccidentDate = ServiceDate.AddDays(-3);

            MccTplScenarios.Apply(claim, scenario);

            Assert.True(claim.Member.DateOfBirth <= ServiceDate.AddYears(-21), scenario.Name);
            Assert.Null(claim.RelatedCausesCode);
            Assert.Null(claim.AccidentDate);
        }
    }

    [Fact]
    public void DiscoveryRequest_CarriesTheStateCoverageCode()
    {
        var scenario = MccTplScenarios.All.Single(s => s.Name == "CaOhcCodeAPayAndChase");
        var claim = CreateClaim();
        MccTplScenarios.Apply(claim, scenario);

        var json = JsonSerializer.SerializeToElement(
            MccTplScenarios.DiscoveryRequest(claim, scenario),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal("CA", json.GetProperty("state").GetString());
        Assert.Equal("A", json.GetProperty("coverage").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("carrierName").GetString()));
        Assert.True(json.GetProperty("effectiveDate").GetDateTime() <= ServiceDate);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("endDate").ValueKind);
    }

    [Theory]
    [InlineData(100, 6)]
    [InlineData(2000, 20)]
    [InlineData(10000, 100)]
    public void RequestedCount_IsAboutOnePercent_WithAtLeastOneOfEach(int corpus, int expected)
        => Assert.Equal(expected, MccTplScenarios.RequestedCount(corpus));

    [Fact]
    public void ForPlanId_IgnoresOtherPlans()
    {
        Assert.Null(MccTplScenarios.ForPlanId(MccWorkflowValidation.CleanProfessionalPaidPlanId));
        Assert.Null(MccTplScenarios.ForPlanId(null));
        Assert.Null(MccTplScenarios.ForPlanId(MccTplScenarios.PlanIdPrefix + "UNKNOWN"));
    }

    [Fact]
    public void ValidatorOptions_TplScenarios_AreOffUnlessAskedFor()
    {
        Assert.False(ValidatorOptions.Parse([]).TplScenariosEnabled);
        Assert.True(ValidatorOptions.Parse(["--tpl-scenarios"]).TplScenariosEnabled);
    }

    private static SyntheticClaim CreateClaim(DateTime? memberDateOfBirth = null) => new()
    {
        ClaimId = "MCC-P-0000042",
        ClaimType = "Professional",
        BenefitPlanId = MccWorkflowValidation.CleanProfessionalPaidPlanId,
        PlaceOfService = "11",
        PriorAuthStatus = "NotRequired",
        DateOfService = ServiceDate,
        PrimaryDiagnosisCode = "Z00.00",
        Member = new SyntheticMember
        {
            MemberId = "MBR-0000001",
            SubscriberId = "SUB-0000001",
            FirstName = "Test",
            LastName = "Member",
            DateOfBirth = memberDateOfBirth ?? new DateTime(1980, 5, 1),
            State = "TX",
            Relationship = "Spouse",
            CoverageEffectiveDate = new DateTime(2025, 1, 1),
        },
        RenderingProvider = new SyntheticProvider { State = "TX" },
        BillingProvider = new SyntheticProvider { State = "TX" },
        Lines = new List<ClaimLine>
        {
            new()
            {
                LineNumber = 1,
                ProcedureCode = "99213",
                PlaceOfService = "11",
                Units = 1,
                ChargeAmount = 180m,
                DiagnosisPointers = new List<int> { 1 },
            },
        },
        ExpectedOutcome = new ExpectedOutcome { Disposition = "Paid" },
    };
}
