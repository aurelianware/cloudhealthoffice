using CloudHealthOffice.BenchmarkClaimGenerator.Generators;
using CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;
using CloudHealthOffice.Tools.MccPlatformValidator;

namespace CloudHealthOffice.MccPlatformValidator.Tests;

public class MccClaimDateNormalizerTests
{
    [Theory]
    [InlineData("NewbornAutoAdjudication")]
    [InlineData("NewbornMotherClaimLink")]
    [InlineData("NewbornFirstThirtyDays")]
    public void NormalizeClaimDates_PreservesNewbornAgeAtServiceDate(string scenario)
    {
        var generator = new EdgeCaseClaimGenerator(new InMemoryReferenceDataProvider());
        var claim = generator.Generate(1, scenario, new Random(42));
        var originalServiceDate = claim.DateOfService.Date;
        var originalAgeAtServiceDays = (claim.DateOfService.Date - claim.Member.DateOfBirth.Date).Days;

        MccClaimDateNormalizer.NormalizeClaimDates([claim], seed: 42);

        Assert.NotEqual(originalServiceDate, claim.DateOfService.Date);
        Assert.Equal(originalAgeAtServiceDays, (claim.DateOfService.Date - claim.Member.DateOfBirth.Date).Days);
        Assert.InRange(originalAgeAtServiceDays, 0, 30);
    }

    [Fact]
    public void NormalizeClaimDates_DoesNotShiftDateOfBirthForNonNewbornEdgeCases()
    {
        var generator = new EdgeCaseClaimGenerator(new InMemoryReferenceDataProvider());
        var claim = generator.Generate(1, "CobPrimaryPayer", new Random(42));
        var originalDateOfBirth = claim.Member.DateOfBirth.Date;

        MccClaimDateNormalizer.NormalizeClaimDates([claim], seed: 42);

        Assert.Equal(originalDateOfBirth, claim.Member.DateOfBirth.Date);
    }
}
