namespace CloudHealthOffice.Portal.Services;

/// <summary>Letters, letter types and templates from correspondence-service.</summary>
public interface ICorrespondenceService
{
    Task<CorrespondenceSummary> GetSummaryAsync();

    /// <summary>The tenant's newest letters the user may read; <paramref name="status"/> is "Rendered" or "CheckFailed".</summary>
    Task<List<CorrespondenceLetter>> GetRecentLettersAsync(string? letterTypeCode = null, string? status = null, int limit = 100);

    Task<List<CorrespondenceLetterType>> GetLetterTypesAsync();
    Task<List<CorrespondenceTemplate>> GetTemplatesAsync();

    /// <summary>Requests one letter (correspondence:write and read access to each subject): the stored letter, or why none was produced.</summary>
    Task<LetterRequestResult> RequestLetterAsync(CorrespondenceLetterRequest request);
}
