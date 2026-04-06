using CloudHealthOffice.BenchmarkClaimGenerator.Generators;
using CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Tests;

public class ProfessionalClaimGeneratorTests
{
    private readonly ProfessionalClaimGenerator _generator;
    private readonly InMemoryReferenceDataProvider _refData;

    public ProfessionalClaimGeneratorTests()
    {
        _refData = new InMemoryReferenceDataProvider();
        _generator = new ProfessionalClaimGenerator(_refData);
    }

    [Fact]
    public void ClaimType_Returns_Professional()
    {
        Assert.Equal("Professional", _generator.ClaimType);
    }

    [Theory]
    [InlineData("officevisit")]
    [InlineData("multiline")]
    [InlineData("globalsurgery")]
    [InlineData("bilateral")]
    [InlineData("assistantsurgeon")]
    [InlineData("telemedicine")]
    [InlineData("labpathology")]
    public void Generate_ProducesValidClaim_ForEachSubType(string subType)
    {
        var random = new Random(42);
        var claim = _generator.Generate(1, subType, random);

        Assert.Equal("MCC-P-0000001", claim.ClaimId);
        Assert.Equal("Professional", claim.ClaimType);
        Assert.Null(claim.EdgeCase);
        Assert.NotNull(claim.Member);
        Assert.NotNull(claim.RenderingProvider);
        Assert.NotNull(claim.BillingProvider);
        Assert.NotEmpty(claim.Lines);
        Assert.NotEmpty(claim.PrimaryDiagnosisCode);
        Assert.True(claim.TotalCharges > 0);
        Assert.NotNull(claim.ExpectedOutcome);
        Assert.True(claim.FhirResourceGenerated);
        Assert.True(claim.PayerToPayerReady);
    }

    [Fact]
    public void Generate_OfficeVisit_HasSingleLine()
    {
        var random = new Random(42);
        var claim = _generator.Generate(1, "officevisit", random);

        Assert.Single(claim.Lines);
        Assert.True(claim.Lines[0].ChargeAmount > 0);
    }

    [Fact]
    public void Generate_MultiLine_HasMultipleLines()
    {
        var random = new Random(42);
        var claim = _generator.Generate(1, "multiline", random);

        Assert.True(claim.Lines.Count >= 2);
    }

    [Fact]
    public void Generate_Bilateral_HasModifier50()
    {
        var random = new Random(42);
        var claim = _generator.Generate(1, "bilateral", random);

        Assert.Contains("50", claim.Lines[0].Modifiers);
    }

    [Fact]
    public void Generate_Telemedicine_HasModifier95()
    {
        var random = new Random(42);
        var claim = _generator.Generate(1, "telemedicine", random);

        Assert.Contains("95", claim.Lines[0].Modifiers);
    }

    [Fact]
    public void Generate_SurgerySubTypes_HavePriorAuth()
    {
        var random = new Random(42);

        var claim = _generator.Generate(1, "globalsurgery", random);
        Assert.Equal("OnFile", claim.PriorAuthStatus);
        Assert.NotNull(claim.PriorAuthNumber);
    }

    [Fact]
    public void Generate_ExpectedOutcome_HasValidAmounts()
    {
        var random = new Random(42);
        var claim = _generator.Generate(1, "officevisit", random);

        Assert.Equal("Paid", claim.ExpectedOutcome.Disposition);
        Assert.True(claim.ExpectedOutcome.ExpectedAllowedAmount > 0);
        Assert.True(claim.ExpectedOutcome.ExpectedPaidAmount >= 0);
        Assert.True(claim.ExpectedOutcome.ExpectedAllowedAmount <= claim.TotalCharges);
    }

    [Fact]
    public void Generate_DeterministicWithSameSeed()
    {
        var claim1 = _generator.Generate(1, "officevisit", new Random(42));
        var claim2 = _generator.Generate(1, "officevisit", new Random(42));

        Assert.Equal(claim1.ClaimId, claim2.ClaimId);
        Assert.Equal(claim1.TotalCharges, claim2.TotalCharges);
        Assert.Equal(claim1.PrimaryDiagnosisCode, claim2.PrimaryDiagnosisCode);
        Assert.Equal(claim1.Member.MemberId, claim2.Member.MemberId);
    }

    [Fact]
    public void Generate_MonetaryAmountsAreRealistic()
    {
        var random = new Random(42);
        var claim = _generator.Generate(1, "officevisit", random);

        // Office visits should be in $25-$450 range
        Assert.InRange(claim.TotalCharges, 25m, 450m);
    }
}
