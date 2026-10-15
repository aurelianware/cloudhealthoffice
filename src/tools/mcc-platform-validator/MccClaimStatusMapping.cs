namespace CloudHealthOffice.Tools.MccPlatformValidator;

internal static class MccClaimStatusMapping
{
    public static ClaimValidationOutcome? ToValidationOutcome(int persistedStatus)
        => persistedStatus switch
        {
            4 => ClaimValidationOutcome.Pended,
            // 5 Approved ("Pay", awaiting the payment cycle), 7 Paid, 9 legacy PartiallyPaid.
            5 or 7 or 9 => ClaimValidationOutcome.Paid,
            // 10 Deny (adjudicated, awaiting the 835), 6 Denied (finalized), 8 Voided.
            6 or 8 or 10 => ClaimValidationOutcome.BusinessDenial,
            _ => null
        };
}
