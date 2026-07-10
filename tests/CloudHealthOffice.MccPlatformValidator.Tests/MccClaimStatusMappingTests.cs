using CloudHealthOffice.Tools.MccPlatformValidator;

namespace CloudHealthOffice.MccPlatformValidator.Tests;

public class MccClaimStatusMappingTests
{
    [Theory]
    [InlineData(4, ClaimValidationOutcome.Pended)]
    [InlineData(5, ClaimValidationOutcome.Paid)]
    [InlineData(7, ClaimValidationOutcome.Paid)]
    [InlineData(9, ClaimValidationOutcome.Paid)]
    [InlineData(6, ClaimValidationOutcome.BusinessDenial)]
    [InlineData(8, ClaimValidationOutcome.BusinessDenial)]
    public void ToValidationOutcome_maps_terminal_claim_statuses_to_validator_outcomes(
        int persistedStatus,
        ClaimValidationOutcome expected)
    {
        Assert.Equal(expected, MccClaimStatusMapping.ToValidationOutcome(persistedStatus));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(999)]
    public void ToValidationOutcome_returns_null_for_non_terminal_statuses(int persistedStatus)
    {
        Assert.Null(MccClaimStatusMapping.ToValidationOutcome(persistedStatus));
    }
}
