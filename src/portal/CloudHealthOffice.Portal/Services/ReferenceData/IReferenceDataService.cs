namespace CloudHealthOffice.Portal.Services;

public interface IReferenceDataService
{
    Task<List<MedicalCode>> SearchCodesAsync(string? codeSystem = null, string? searchTerm = null);
    Task<MedicalCodeDetails?> GetCodeDetailsAsync(string codeSystem, string code);
    Task<List<string>> GetCodeSystemsAsync();
    Task<CodeUsageStats> GetCodeUsageStatsAsync(string codeSystem, string code);
}
