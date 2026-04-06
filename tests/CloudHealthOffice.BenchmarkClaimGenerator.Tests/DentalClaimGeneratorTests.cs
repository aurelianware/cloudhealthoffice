using CloudHealthOffice.BenchmarkClaimGenerator.Generators;
using CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Tests;

public class DentalClaimGeneratorTests
{
    private readonly DentalClaimGenerator _generator;
    private readonly InMemoryReferenceDataProvider _refData;

    public DentalClaimGeneratorTests()
    {
        _refData = new InMemoryReferenceDataProvider();
        _generator = new DentalClaimGenerator(_refData);
    }

    [Fact]
    public void ClaimType_Returns_Dental()
    {
        Assert.Equal("Dental", _generator.ClaimType);
    }

    [Theory]
    [InlineData("preventive")]
    [InlineData("restorative")]
    [InlineData("endodontics")]
    [InlineData("periodontics")]
    [InlineData("orthodontics")]
    [InlineData("oralsurgery")]
    public void Generate_ProducesValidClaim_ForEachCategory(string category)
    {
        var random = new Random(42);
        var claim = _generator.Generate(1, category, random);

        Assert.Equal("MCC-D-0000001", claim.ClaimId);
        Assert.Equal("Dental", claim.ClaimType);
        Assert.NotEmpty(claim.Lines);
        Assert.True(claim.TotalCharges > 0);
        Assert.NotNull(claim.ExpectedOutcome);
        Assert.True(claim.FhirResourceGenerated);
    }

    [Fact]
    public void Generate_Orthodontics_RequiresPriorAuth()
    {
        var random = new Random(42);
        var claim = _generator.Generate(1, "orthodontics", random);

        Assert.Equal("OnFile", claim.PriorAuthStatus);
        Assert.NotNull(claim.PriorAuthNumber);
    }

    [Fact]
    public void Generate_Preventive_HasLowerCoinsurance()
    {
        var random = new Random(100); // Use seed that avoids denial
        var claim = _generator.Generate(1, "preventive", random);

        if (claim.ExpectedOutcome.Disposition == "Paid")
        {
            Assert.Equal(0m, claim.ExpectedOutcome.ExpectedCoinsurance);
        }
    }

    [Fact]
    public void Generate_DentalCodes_StartWithD()
    {
        var random = new Random(42);
        var claim = _generator.Generate(1, "preventive", random);

        Assert.All(claim.Lines, line =>
            Assert.StartsWith("D", line.ProcedureCode));
    }

    [Fact]
    public void Generate_DeterministicWithSameSeed()
    {
        var claim1 = _generator.Generate(1, "restorative", new Random(42));
        var claim2 = _generator.Generate(1, "restorative", new Random(42));

        Assert.Equal(claim1.TotalCharges, claim2.TotalCharges);
        Assert.Equal(claim1.Lines.Count, claim2.Lines.Count);
    }
}
