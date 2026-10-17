using System.Net.Http.Json;

namespace CloudHealthOffice.Portal.Services;

// OperatingModeService intentionally returns defaults when tenant-service is unreachable.
// Missing config should default to Replace mode, not error.
public class OperatingModeService : IOperatingModeService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OperatingModeService> _logger;

    public OperatingModeService(HttpClient httpClient, IConfiguration configuration, ILogger<OperatingModeService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<OperatingModeConfiguration> GetOperatingModeAsync(string tenantId)
    {
        var baseUrl = _configuration["Services:TenantService"];
        try
        {
            var result = await _httpClient.GetFromJsonAsync<OperatingModeConfiguration>(
                $"{baseUrl}/v1/tenants/{tenantId}/operating-mode");
            if (result != null)
                return NormalizeConfiguration(result, tenantId);

            return GetDefaultConfiguration(tenantId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to fetch operating mode for tenant {TenantId}, returning defaults", tenantId);
            return GetDefaultConfiguration(tenantId);
        }
    }

    private static OperatingModeConfiguration NormalizeConfiguration(OperatingModeConfiguration config, string tenantId)
    {
        var merged = new Dictionary<string, string>(OperatingModeConfiguration.DefaultEngines, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in config.Engines)
        {
            merged[kvp.Key] = kvp.Value;
        }

        config.TenantId = string.IsNullOrEmpty(config.TenantId) ? tenantId : config.TenantId;
        config.Engines = merged;
        return config;
    }

    private static OperatingModeConfiguration GetDefaultConfiguration(string tenantId)
    {
        return new OperatingModeConfiguration
        {
            TenantId = tenantId,
            Engines = new Dictionary<string, string>(OperatingModeConfiguration.DefaultEngines, StringComparer.OrdinalIgnoreCase),
            UpdatedAt = null
        };
    }
}
