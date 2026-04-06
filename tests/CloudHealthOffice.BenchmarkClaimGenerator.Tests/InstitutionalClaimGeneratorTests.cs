using CloudHealthOffice.BenchmarkClaimGenerator.Generators;
using CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Tests;

public class InstitutionalClaimGeneratorTests
{
    private readonly InstitutionalClaimGenerator _generator;
    private readonly InMemoryReferenceDataProvider _refData;

    public InstitutionalClaimGeneratorTests()
    {
        _refData = new InMemoryReferenceDataProvider();
        _generator = new InstitutionalClaimGenerator(_refData);
    }

    [Fact]
    public void ClaimType_Returns_Institutional()
    {
        Assert.Equal("Institutional", _generator.ClaimType);
    }

    [Theory]
    [InlineData("inpatient")]
    [InlineData("outpatient")]
    [InlineData("emergency")]
    [InlineData("observation")]
    [InlineData("stoploss")]
    [InlineData("skillednursing")]
    public void Generate_ProducesValidClaim_ForEachSubType(string subType)
    {
        var random = new Random(42);
        var claim = _generator.Generate(1, subType, random);

        Assert.Equal("MCC-I-0000001", claim.ClaimId);
        Assert.Equal("Institutional", claim.ClaimType);
        Assert.NotNull(claim.BillType);
        Assert.NotEmpty(claim.Lines);
        Assert.True(claim.TotalCharges > 0);
        Assert.NotNull(claim.ExpectedOutcome);
        Assert.True(claim.FhirResourceGenerated);
    }

    [Fact]
    public void Generate_Inpatient_HasDrgCode()
    {
        var random = new Random(42);
        var claim = _generator.Generate(1, "inpatient", random);

        Assert.NotNull(claim.DrgCode);
        Assert.Equal("0111", claim.BillType);
        Assert.Equal("OnFile", claim.PriorAuthStatus);
    }

    [Fact]
    public void Generate_Inpatient_HasRealisticCharges()
    {
        var random = new Random(42);
        var claim = _generator.Generate(1, "inpatient", random);

        // Inpatient should be $5K-$150K range
        Assert.True(claim.TotalCharges >= 2000m, $"Inpatient charges too low: {claim.TotalCharges}");
    }

    [Fact]
    public void Generate_Emergency_HasEmergencyBillType()
    {
        var random = new Random(42);
        var claim = _generator.Generate(1, "emergency", random);

        Assert.Equal("0131", claim.BillType);
    }

    [Fact]
    public void Generate_Lines_HaveRevenueCodes()
    {
        var random = new Random(42);
        var claim = _generator.Generate(1, "inpatient", random);

        Assert.All(claim.Lines, line => Assert.NotNull(line.RevenueCode));
    }

    [Fact]
    public void Generate_ExpectedOutcome_HasDrgCode_ForInpatient()
    {
        var random = new Random(42);
        var claim = _generator.Generate(1, "inpatient", random);

        Assert.NotNull(claim.ExpectedOutcome.ExpectedDrgCode);
        Assert.Equal(claim.DrgCode, claim.ExpectedOutcome.ExpectedDrgCode);
    }

    [Fact]
    public void Generate_DeterministicWithSameSeed()
    {
        var claim1 = _generator.Generate(1, "inpatient", new Random(42));
        var claim2 = _generator.Generate(1, "inpatient", new Random(42));

        Assert.Equal(claim1.TotalCharges, claim2.TotalCharges);
        Assert.Equal(claim1.DrgCode, claim2.DrgCode);
    }
}
