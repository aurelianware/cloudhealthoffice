namespace BenefitPlanService.Services;

/// <summary>
/// Pre-pricing terminology crosswalk client. Resolves plan-specific code
/// mappings through the TerminologyService before the FeeScheduleEngine runs.
///
/// For TX Medicaid, this translates plan-specific procedure code overrides
/// that affect rate lookup. Without this step, the FeeScheduleEngine would
/// look up the original code and potentially miss the contracted rate.
/// </summary>
public interface ITerminologyCrosswalkClient
{
    /// <summary>
    /// Translate a batch of procedure codes through plan-specific crosswalks.
    /// Returns the translated codes (or the original if no mapping exists).
    /// </summary>
    Task<List<CodeCrosswalkResult>> TranslateBatchAsync(
        string tenantId,
        List<CodeCrosswalkRequest> requests,
        CancellationToken ct = default);
}

public record CodeCrosswalkRequest
{
    public int LineNumber { get; init; }
    public string ProcedureCode { get; init; } = string.Empty;
    public string CodeType { get; init; } = "CPT";
}

public record CodeCrosswalkResult
{
    public int LineNumber { get; init; }
    public string OriginalCode { get; init; } = string.Empty;
    public string ResolvedCode { get; init; } = string.Empty;
    public bool WasTranslated { get; init; }
    public string? MapVersionId { get; init; }
}
