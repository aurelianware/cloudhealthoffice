namespace CloudHealthOffice.Tools.MccPlatformValidator;

internal static class MccClaimStatusMapping
{
    public static ClaimValidationOutcome? ToValidationOutcome(int persistedStatus)
        => persistedStatus switch
        {
            4 => ClaimValidationOutcome.Pended,
            5 or 7 or 9 => ClaimValidationOutcome.Paid,
            6 or 8 => ClaimValidationOutcome.BusinessDenial,
            _ => null
        };
}
