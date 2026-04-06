using CloudHealthOffice.BenchmarkClaimGenerator.Generators;
using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Tests;

public class EdgeCaseClaimGeneratorTests
{
    private readonly EdgeCaseClaimGenerator _generator;
    private readonly InMemoryReferenceDataProvider _refData;

    public EdgeCaseClaimGeneratorTests()
    {
        _refData = new InMemoryReferenceDataProvider();
        _generator = new EdgeCaseClaimGenerator(_refData);
    }

    [Fact]
    public void ClaimType_Returns_EdgeCase()
    {
        Assert.Equal("EdgeCase", _generator.ClaimType);
    }

    [Theory]
    [InlineData("CobPrimaryPayer", "Paid")]
    [InlineData("CobSecondaryPayer", "Pended")]
    [InlineData("RetroEligibilityTermination", "Denied")]
    [InlineData("PriorAuthRequired_AuthOnFile", "Paid")]
    [InlineData("PriorAuthRequired_NoAuth", "Denied")]
    [InlineData("SubrogationAccidentRelated", "Pended")]
    [InlineData("BehavioralHealthCarveOut", "Denied")]
    [InlineData("BehavioralHealthCarveIn", "Paid")]
    [InlineData("MedicaidTANF", "Paid")]
    [InlineData("MedicaidDualEligible", "Pended")]
    public void Generate_ProducesCorrectDisposition(string scenario, string expectedDisposition)
    {
        var random = new Random(42);
        var claim = _generator.Generate(1, scenario, random);

        Assert.Equal(expectedDisposition, claim.ExpectedOutcome.Disposition);
    }

    [Theory]
    [InlineData("CobPrimaryPayer", EdgeCaseScenario.CobPrimaryPayer)]
    [InlineData("NewbornAutoAdjudication", EdgeCaseScenario.NewbornAutoAdjudication)]
    [InlineData("PriorAuthRequired_ExpiredAuth", EdgeCaseScenario.PriorAuthRequired_ExpiredAuth)]
    [InlineData("SubrogationWorkersComp", EdgeCaseScenario.SubrogationWorkersComp)]
    [InlineData("MedicaidCHIP", EdgeCaseScenario.MedicaidCHIP)]
    public void Generate_SetsEdgeCaseProperty(string scenarioName, EdgeCaseScenario expected)
    {
        var random = new Random(42);
        var claim = _generator.Generate(1, scenarioName, random);

        Assert.Equal("MCC-E-0000001", claim.ClaimId);
        Assert.Equal("EdgeCase", claim.ClaimType);
        Assert.Equal(expected, claim.EdgeCase);
    }

    [Fact]
    public void Generate_PriorAuth_NoAuth_IsDenied_WithReasonCode()
    {
        var random = new Random(42);
        var claim = _generator.Generate(1, "PriorAuthRequired_NoAuth", random);

        Assert.Equal("Denied", claim.ExpectedOutcome.Disposition);
        Assert.Equal("197", claim.ExpectedOutcome.DenialReasonCode);
        Assert.Equal("Required", claim.PriorAuthStatus);
        Assert.Null(claim.PriorAuthNumber);
    }

    [Fact]
    public void Generate_PriorAuth_ExpiredAuth_IsDenied()
    {
        var random = new Random(42);
        var claim = _generator.Generate(1, "PriorAuthRequired_ExpiredAuth", random);

        Assert.Equal("Denied", claim.ExpectedOutcome.Disposition);
        Assert.Equal("Expired", claim.PriorAuthStatus);
    }

    [Fact]
    public void Generate_Newborn_HasChildRelationship()
    {
        var random = new Random(42);
        var claim = _generator.Generate(1, "NewbornAutoAdjudication", random);

        Assert.Equal("Child", claim.Member.Relationship);
    }

    [Fact]
    public void Generate_CobSecondary_PendsForReview()
    {
        var random = new Random(42);
        var claim = _generator.Generate(1, "CobSecondaryPayer", random);

        Assert.Equal("Pended", claim.ExpectedOutcome.Disposition);
        Assert.Equal("22", claim.ExpectedOutcome.DenialReasonCode);
    }

    [Fact]
    public void Generate_DeniedClaims_HaveZeroPaidAmount()
    {
        var random = new Random(42);
        var claim = _generator.Generate(1, "RetroEligibilityTermination", random);

        Assert.Equal("Denied", claim.ExpectedOutcome.Disposition);
        Assert.Equal(0m, claim.ExpectedOutcome.ExpectedPaidAmount);
        Assert.Equal(0m, claim.ExpectedOutcome.ExpectedAllowedAmount);
    }

    [Fact]
    public void Generate_AllScenarios_ProduceValidClaims()
    {
        var random = new Random(42);
        foreach (var scenario in Enum.GetValues<EdgeCaseScenario>())
        {
            var claim = _generator.Generate(1, scenario.ToString(), new Random(42));
            Assert.NotNull(claim);
            Assert.Equal("EdgeCase", claim.ClaimType);
            Assert.Equal(scenario, claim.EdgeCase);
            Assert.NotNull(claim.ExpectedOutcome);
            Assert.True(claim.FhirResourceGenerated);
        }
    }

    [Fact]
    public void Generate_DeterministicWithSameSeed()
    {
        var claim1 = _generator.Generate(1, "CobPrimaryPayer", new Random(42));
        var claim2 = _generator.Generate(1, "CobPrimaryPayer", new Random(42));

        Assert.Equal(claim1.TotalCharges, claim2.TotalCharges);
        Assert.Equal(claim1.Member.MemberId, claim2.Member.MemberId);
    }
}
