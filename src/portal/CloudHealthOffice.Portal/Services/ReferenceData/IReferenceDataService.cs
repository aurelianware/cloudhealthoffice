namespace CloudHealthOffice.Portal.Services;

/// <summary>Code lookups in reference-data-service (api/reference-data/codes).</summary>
public interface IReferenceDataService
{
    /// <summary>One page of a code system's codes matching <paramref name="search"/>.</summary>
    Task<ReferenceCodePage> SearchCodesAsync(string codeSystem, string? search, ReferenceSearchMode mode,
        bool? active = null, int page = 1, int pageSize = 50);

    /// <summary>The version of a code effective today, or null.</summary>
    Task<ReferenceCodeRecord?> GetCodeAsync(string codeSystem, string code);
}
