using CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Tests;

public class DiagnosisCodesTests
{
    [Theory]
    [InlineData("K08.1", "Complete loss of teeth")]
    [InlineData("k08.1", "Complete loss of teeth")]
    [InlineData("Z38.00", "Single liveborn infant, delivered vaginally")]
    public void FindDescription_ReturnsSyntheticDiagnosisDescription(string code, string expectedDescription)
    {
        Assert.Equal(expectedDescription, DiagnosisCodes.FindDescription(code));
    }

    [Fact]
    public void FindDescription_ReturnsNullForUnknownCode()
    {
        Assert.Null(DiagnosisCodes.FindDescription("ZZZ.999"));
    }
}
