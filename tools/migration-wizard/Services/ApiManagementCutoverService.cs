using Azure.Identity;
using MigrationWizard.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MigrationWizard.Services;

/// <summary>
/// Service to handle API Management routing key flip for cutover
/// </summary>
public class ApiManagementCutoverService
{
    private readonly ApiManagementConfig _config;
    private readonly ILogger<ApiManagementCutoverService> _logger;
    private readonly HttpClient _httpClient;

    public ApiManagementCutoverService(
        ApiManagementConfig config,
        ILogger<ApiManagementCutoverService> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = new HttpClient();
    }

    /// <summary>
    /// Execute cutover by flipping routing keys in API Management
    /// </summary>
    public async Task<CutoverResult> ExecuteCutoverAsync(CancellationToken cancellationToken = default)
    {
        var result = new CutoverResult
        {
            StartedAt = DateTime.UtcNow
        };

        try
        {
            _logger.LogInformation("Starting API Management cutover for service {ServiceName}", _config.ServiceName);

            // Step 1: Authenticate with Azure
            var token = await GetAzureAccessTokenAsync(cancellationToken);
            if (string.IsNullOrEmpty(token))
            {
                result.Success = false;
                result.ErrorMessage = "Failed to authenticate with Azure";
                return result;
            }

            // Step 2: Get current routing configuration
            var currentConfig = await GetCurrentRoutingConfigAsync(token, cancellationToken);
            result.PreviousBackendId = currentConfig.CurrentBackendId;

            // Step 3: Update Named Value (routing key)
            var updateSuccess = await UpdateRoutingKeyAsync(token, _config.CloudHealthOfficeBackendId, cancellationToken);
            if (!updateSuccess)
            {
                result.Success = false;
                result.ErrorMessage = "Failed to update routing key";
                return result;
            }

            // Step 4: Verify the change
            var verifiedConfig = await GetCurrentRoutingConfigAsync(token, cancellationToken);
            if (verifiedConfig.CurrentBackendId != _config.CloudHealthOfficeBackendId)
            {
                result.Success = false;
                result.ErrorMessage = "Routing key update verification failed";
                return result;
            }

            result.Success = true;
            result.NewBackendId = _config.CloudHealthOfficeBackendId;
            result.CompletedAt = DateTime.UtcNow;

            _logger.LogInformation("Cutover completed successfully. Traffic now routed to {BackendId}", 
                _config.CloudHealthOfficeBackendId);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cutover failed");
            result.Success = false;
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    /// <summary>
    /// Rollback cutover by reverting to legacy backend
    /// </summary>
    public async Task<CutoverResult> RollbackCutoverAsync(CancellationToken cancellationToken = default)
    {
        var result = new CutoverResult
        {
            StartedAt = DateTime.UtcNow
        };

        try
        {
            _logger.LogWarning("Starting cutover rollback to legacy backend");

            var token = await GetAzureAccessTokenAsync(cancellationToken);
            if (string.IsNullOrEmpty(token))
            {
                result.Success = false;
                result.ErrorMessage = "Failed to authenticate with Azure";
                return result;
            }

            var currentConfig = await GetCurrentRoutingConfigAsync(token, cancellationToken);
            result.PreviousBackendId = currentConfig.CurrentBackendId;

            var updateSuccess = await UpdateRoutingKeyAsync(token, _config.LegacyBackendId, cancellationToken);
            if (!updateSuccess)
            {
                result.Success = false;
                result.ErrorMessage = "Failed to rollback routing key";
                return result;
            }

            result.Success = true;
            result.NewBackendId = _config.LegacyBackendId;
            result.CompletedAt = DateTime.UtcNow;

            _logger.LogInformation("Rollback completed. Traffic now routed to {BackendId}", _config.LegacyBackendId);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rollback failed");
            result.Success = false;
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    /// <summary>
    /// Get current routing status
    /// </summary>
    public async Task<RoutingStatus> GetRoutingStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var token = await GetAzureAccessTokenAsync(cancellationToken);
            if (string.IsNullOrEmpty(token))
            {
                return new RoutingStatus
                {
                    IsConfigured = false,
                    ErrorMessage = "Unable to authenticate with Azure"
                };
            }

            var config = await GetCurrentRoutingConfigAsync(token, cancellationToken);
            
            return new RoutingStatus
            {
                IsConfigured = true,
                CurrentBackendId = config.CurrentBackendId,
                IsRoutedToCloudHealthOffice = config.CurrentBackendId == _config.CloudHealthOfficeBackendId,
                LastUpdated = config.LastUpdated
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get routing status");
            return new RoutingStatus
            {
                IsConfigured = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Validate API Management configuration
    /// </summary>
    public async Task<bool> ValidateConfigurationAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Validating API Management configuration");

            var token = await GetAzureAccessTokenAsync(cancellationToken);
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogError("Failed to obtain Azure access token");
                return false;
            }

            // Verify API Management service exists
            var serviceUrl = $"https://management.azure.com/subscriptions/{_config.SubscriptionId}" +
                $"/resourceGroups/{_config.ResourceGroup}/providers/Microsoft.ApiManagement/service/{_config.ServiceName}" +
                "?api-version=2022-08-01";

            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            
            var response = await _httpClient.GetAsync(serviceUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("API Management service not found or not accessible");
                return false;
            }

            _logger.LogInformation("API Management configuration validated successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Configuration validation failed");
            return false;
        }
    }

    private async Task<string?> GetAzureAccessTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            var credential = new DefaultAzureCredential();
            var tokenRequestContext = new Azure.Core.TokenRequestContext(
                new[] { "https://management.azure.com/.default" });
            
            var token = await credential.GetTokenAsync(tokenRequestContext, cancellationToken);
            return token.Token;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to obtain Azure access token");
            return null;
        }
    }

    private async Task<RoutingConfig> GetCurrentRoutingConfigAsync(string token, CancellationToken cancellationToken)
    {
        var namedValueUrl = $"https://management.azure.com/subscriptions/{_config.SubscriptionId}" +
            $"/resourceGroups/{_config.ResourceGroup}/providers/Microsoft.ApiManagement/service/{_config.ServiceName}" +
            $"/namedValues/{_config.RoutingKeyName}?api-version=2022-08-01";

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        
        var response = await _httpClient.GetAsync(namedValueUrl, cancellationToken);
        
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var json = JsonDocument.Parse(content);
            
            var currentValue = json.RootElement
                .GetProperty("properties")
                .GetProperty("value")
                .GetString();

            return new RoutingConfig
            {
                CurrentBackendId = currentValue ?? _config.LegacyBackendId,
                LastUpdated = DateTime.UtcNow
            };
        }

        // If named value doesn't exist, assume legacy backend
        return new RoutingConfig
        {
            CurrentBackendId = _config.LegacyBackendId,
            LastUpdated = DateTime.UtcNow
        };
    }

    private async Task<bool> UpdateRoutingKeyAsync(string token, string newBackendId, CancellationToken cancellationToken)
    {
        var namedValueUrl = $"https://management.azure.com/subscriptions/{_config.SubscriptionId}" +
            $"/resourceGroups/{_config.ResourceGroup}/providers/Microsoft.ApiManagement/service/{_config.ServiceName}" +
            $"/namedValues/{_config.RoutingKeyName}?api-version=2022-08-01";

        var payload = new
        {
            properties = new
            {
                displayName = _config.RoutingKeyName,
                value = newBackendId,
                secret = false,
                tags = new[] { "migration", "routing" }
            }
        };

        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        
        var response = await _httpClient.PutAsync(namedValueUrl, content, cancellationToken);
        
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to update routing key: {Error}", error);
            return false;
        }

        return true;
    }
}

public class CutoverResult
{
    public bool Success { get; set; }
    public string? PreviousBackendId { get; set; }
    public string? NewBackendId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
}

public class RoutingStatus
{
    public bool IsConfigured { get; set; }
    public string? CurrentBackendId { get; set; }
    public bool IsRoutedToCloudHealthOffice { get; set; }
    public DateTime? LastUpdated { get; set; }
    public string? ErrorMessage { get; set; }
}

public class RoutingConfig
{
    public string CurrentBackendId { get; set; } = string.Empty;
    public DateTime LastUpdated { get; set; }
}
