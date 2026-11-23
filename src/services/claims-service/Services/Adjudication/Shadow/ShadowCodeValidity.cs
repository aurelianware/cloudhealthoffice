namespace ClaimsService.Services.Adjudication.Shadow;

/// <summary>
/// Codes the core accepted that were not valid on the date of service. The core
/// paid the claim, or a line, while it carried a diagnosis or procedure code
/// that CMS had not yet released, had deleted, or (for ICD-10) only lists as a
/// non-billable header. A possible unsupported payment and a recoupment lead,
/// whatever the two sides decided. When CHO's mode is Report (the default) the
/// two outcomes still match, so this finding is the only place it shows.
/// Leads for the plan's review, not determinations.
/// </summary>
public static class ShadowCodeValidity
{
    public const string CorePaidInvalidCode = "Core paid with a code not valid on the date of service";

    /// <summary>The claim's code-validity finding, or null when there is none.</summary>
    public static string? For(ShadowAdjudicationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.CoreOutcome == ShadowOutcome.Paid && result.CodeValidityFindings.Count > 0
            ? CorePaidInvalidCode
            : null;
    }

    /// <summary>The findings as one cell: "Line 2 HCPCS J9999: terminated | Diagnosis ICD-10-CM E11: not-billable".</summary>
    public static string? Describe(ShadowAdjudicationResult result) =>
        result.CodeValidityFindings.Count == 0
            ? null
            : string.Join(" | ", result.CodeValidityFindings.Select(f => f.Label()).Distinct(StringComparer.Ordinal));

    /// <summary>The reasons on the claim, for grouping ("terminated", "not-billable | not-in-code-set").</summary>
    public static string ReasonKey(ShadowAdjudicationResult result) =>
        string.Join(" | ", result.CodeValidityFindings.Select(f => f.Reason).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
}
