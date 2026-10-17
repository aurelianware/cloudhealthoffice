namespace CloudHealthOffice.Portal.Services;

public interface IPricingApiService
{
    Task<List<PricingApiKey>> GetApiKeysAsync();
    Task<PricingApiKey> CreateApiKeyAsync(string tenantName, string contactEmail, string tier);
    /// <summary>Deactivates a key by its key id (never the key itself).</summary>
    Task DeactivateApiKeyAsync(string keyId);
    Task ResetUsageAsync();
    Task<List<PricingFeeScheduleInfo>> GetFeeSchedulesAsync();
    Task<FeeScheduleUploadResult> UploadFeeScheduleAsync(string type, int year, Stream csvStream, string fileName, decimal? baseRate = null);
    Task SeedDemoDataAsync();
}
