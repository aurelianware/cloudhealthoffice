using MemberDocumentService.Services;

namespace MemberDocumentService.Tests.Services;

public class RetentionPolicyServiceTests
{
    private readonly IRetentionPolicyService _service = new RetentionPolicyService();

    [Fact]
    public void Default_Returns10Years()
    {
        var result = _service.ResolvePolicy(null, null, null);
        result.YearsToRetain.Should().Be(10);
        result.PolicyId.Should().Be("DEFAULT-10Y");
        result.RetentionUntilDate.Date.Should().Be(DateTime.UtcNow.Date.AddYears(10));
    }

    [Theory]
    [InlineData("TX", 10)]
    [InlineData("CA", 10)]
    [InlineData("NY", 10)]
    [InlineData("FL", 10)]   // no override — falls back to 10
    [InlineData("ZZ", 10)]   // unknown state — falls back to 10
    public void StateRetentionMatrixMatchesExpected(string stateCode, int expectedYears)
    {
        var result = _service.ResolvePolicy(stateCode, null, null);
        result.YearsToRetain.Should().BeGreaterThanOrEqualTo(expectedYears);
    }

    [Fact]
    public void CoverageTerminationDate_UsedAsBase()
    {
        var terminationDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = _service.ResolvePolicy("TX", terminationDate, null);
        result.RetentionUntilDate.Date.Should().Be(terminationDate.AddYears(result.YearsToRetain).Date);
    }

    [Fact]
    public void ExplicitPolicyId_IsPreserved()
    {
        var result = _service.ResolvePolicy("TX", null, "CUSTOM-5Y");
        result.PolicyId.Should().Be("CUSTOM-5Y");
    }

    [Fact]
    public void HipaaFloor_AlwaysEnforced()
    {
        // Even if no state or explicit override, minimum is 6 years.
        var result = _service.ResolvePolicy(null, null, null);
        result.YearsToRetain.Should().BeGreaterThanOrEqualTo(6);
    }
}
