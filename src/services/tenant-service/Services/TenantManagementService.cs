using System.Security.Cryptography;
using System.Text;
using CloudHealthOffice.OperatingMode;
using TenantService.Models;

namespace TenantService.Services;

public class TenantManagementService : ITenantService
{
    private readonly ITenantRepository _repository;
    private readonly ILogger<TenantManagementService> _logger;
    private readonly ISftpProvisioningService _sftpProvisioning;

    public TenantManagementService(
        ITenantRepository repository, 
        ILogger<TenantManagementService> logger,
        ISftpProvisioningService sftpProvisioning)
    {
        _repository = repository;
        _logger = logger;
        _sftpProvisioning = sftpProvisioning;
    }

    public async Task<Tenant> CreateTenantAsync(CreateTenantRequest request)
    {
        // Generate unique tenant ID
        var tenantId = GenerateTenantId(request.OrganizationName);

        // Check if tenant already exists
        if (await _repository.ExistsAsync(tenantId))
        {
            throw new InvalidOperationException($"Tenant with ID {tenantId} already exists");
        }

        var tenant = new Tenant
        {
            TenantId = tenantId,
            TenantName = request.TenantName,
            OrganizationName = request.OrganizationName,
            SubscriptionTier = request.SubscriptionTier.ToLower(),
            Status = "pending", // Will be activated after setup complete
            ContactInfo = request.ContactInfo,
            Configuration = new TenantConfiguration
            {
                EnabledModules = request.EnabledModules ?? new List<string> { "claims", "eligibility" },
                Clearinghouse = request.Clearinghouse
            }
        };

        var created = await _repository.CreateAsync(tenant);
        _logger.LogInformation("Created tenant {TenantId} for {OrganizationName}", SanitizeForLog(tenantId), SanitizeForLog(request.OrganizationName));

        // Provision SFTP access with multi-environment support
        var environments = request.Environments ?? new List<string> { "prod" };
        _logger.LogInformation("Provisioning SFTP for tenant {TenantId} with environments: {Environments}",
            SanitizeForLog(tenantId), string.Join(",", environments));
        
        try
        {
            var provisioningResult = await _sftpProvisioning.ProvisionTenantSftpAsync(
                tenantId, 
                request.OrganizationName, 
                environments);

            if (provisioningResult.Success)
            {
                _logger.LogInformation("SFTP provisioned successfully for tenant {TenantId}", tenantId);
                
                // Store SFTP metadata in tenant configuration
                created.Configuration.SftpProvisioned = true;
                created.Configuration.SftpEnvironments = environments;
                await _repository.UpdateAsync(created);
            }
            else
            {
                _logger.LogError("SFTP provisioning failed for tenant {TenantId}: {Error}", 
                    tenantId, provisioningResult.Error);
                // Don't fail tenant creation, but log for manual followup
                created.Status = "pending-sftp";
                await _repository.UpdateAsync(created);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during SFTP provisioning for tenant {TenantId}", tenantId);
            // Don't fail tenant creation, mark for manual provisioning
            created.Status = "pending-sftp";
            await _repository.UpdateAsync(created);
        }

        return created;
    }

    public async Task<Tenant?> GetTenantAsync(string tenantId)
    {
        return await _repository.GetByTenantIdAsync(tenantId);
    }

    public async Task<IEnumerable<Tenant>> GetAllTenantsAsync()
    {
        return await _repository.GetAllAsync();
    }

    public async Task<Tenant> UpdateTenantAsync(string tenantId, UpdateTenantRequest request)
    {
        var tenant = await _repository.GetByTenantIdAsync(tenantId);
        if (tenant == null)
        {
            throw new KeyNotFoundException($"Tenant {tenantId} not found");
        }

        // Update fields if provided
        if (!string.IsNullOrEmpty(request.TenantName))
            tenant.TenantName = request.TenantName;

        if (!string.IsNullOrEmpty(request.OrganizationName))
            tenant.OrganizationName = request.OrganizationName;

        if (!string.IsNullOrEmpty(request.SubscriptionTier))
            tenant.SubscriptionTier = request.SubscriptionTier.ToLower();

        if (!string.IsNullOrEmpty(request.Status))
            tenant.Status = request.Status.ToLower();

        if (request.ContactInfo != null)
            tenant.ContactInfo = request.ContactInfo;

        if (request.Configuration != null)
            tenant.Configuration = request.Configuration;

        return await _repository.UpdateAsync(tenant);
    }

    public async Task ActivateTenantAsync(string tenantId)
    {
        var tenant = await _repository.GetByTenantIdAsync(tenantId);
        if (tenant == null)
        {
            throw new KeyNotFoundException($"Tenant {tenantId} not found");
        }

        tenant.Status = "active";
        tenant.ActivatedAt = DateTime.UtcNow;
        
        await _repository.UpdateAsync(tenant);
        _logger.LogInformation("Activated tenant {TenantId}", SanitizeForLog(tenantId));
    }

    public async Task SuspendTenantAsync(string tenantId)
    {
        var tenant = await _repository.GetByTenantIdAsync(tenantId);
        if (tenant == null)
        {
            throw new KeyNotFoundException($"Tenant {tenantId} not found");
        }

        tenant.Status = "suspended";
        
        await _repository.UpdateAsync(tenant);
        _logger.LogWarning("Suspended tenant {TenantId}", SanitizeForLog(tenantId));
    }

    public async Task DeleteTenantAsync(string tenantId)
    {
        await _repository.DeleteAsync(tenantId);
        _logger.LogWarning("Deleted tenant {TenantId}", SanitizeForLog(tenantId));
    }

    public async Task<ApiKeyResponse> CreateApiKeyAsync(string tenantId, CreateApiKeyRequest request)
    {
        var tenant = await _repository.GetByTenantIdAsync(tenantId);
        if (tenant == null)
        {
            throw new KeyNotFoundException($"Tenant {tenantId} not found");
        }

        // Generate API key
        var apiKey = GenerateApiKey();
        var keyHash = HashApiKey(apiKey);
        var keyPrefix = apiKey.Substring(0, 8);

        var apiKeyRecord = new ApiKey
        {
            Name = request.Name,
            KeyHash = keyHash,
            KeyPrefix = keyPrefix,
            ExpiresAt = request.ExpiresAt,
            Scopes = request.Scopes ?? new List<string>(),
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };

        tenant.ApiKeys.Add(apiKeyRecord);
        await _repository.UpdateAsync(tenant);

        _logger.LogInformation("Created API key {KeyId} for tenant {TenantId}", SanitizeForLog(apiKeyRecord.KeyId), SanitizeForLog(tenantId));

        return new ApiKeyResponse
        {
            KeyId = apiKeyRecord.KeyId,
            Name = apiKeyRecord.Name,
            ApiKey = apiKey, // Only returned once!
            CreatedAt = apiKeyRecord.CreatedAt,
            ExpiresAt = apiKeyRecord.ExpiresAt,
            Scopes = apiKeyRecord.Scopes
        };
    }

    public async Task<IEnumerable<ApiKey>> GetApiKeysAsync(string tenantId)
    {
        var tenant = await _repository.GetByTenantIdAsync(tenantId);
        if (tenant == null)
        {
            throw new KeyNotFoundException($"Tenant {tenantId} not found");
        }

        return tenant.ApiKeys;
    }

    public async Task RevokeApiKeyAsync(string tenantId, string keyId)
    {
        var tenant = await _repository.GetByTenantIdAsync(tenantId);
        if (tenant == null)
        {
            throw new KeyNotFoundException($"Tenant {tenantId} not found");
        }

        var apiKey = tenant.ApiKeys.FirstOrDefault(k => k.KeyId == keyId);
        if (apiKey != null)
        {
            apiKey.IsActive = false;
            await _repository.UpdateAsync(tenant);
            _logger.LogInformation("Revoked API key {KeyId} for tenant {TenantId}", SanitizeForLog(keyId), SanitizeForLog(tenantId));
        }
    }

    public async Task<Tenant?> ValidateApiKeyAsync(string apiKey)
    {
        var keyHash = HashApiKey(apiKey);
        var tenant = await _repository.GetByApiKeyHashAsync(keyHash);

        if (tenant == null)
        {
            _logger.LogWarning("Invalid API key attempt");
            return null;
        }

        var key = tenant.ApiKeys.FirstOrDefault(k => k.KeyHash == keyHash);
        if (key == null || !key.IsActive)
        {
            _logger.LogWarning("Inactive or revoked API key for tenant {TenantId}", tenant.TenantId);
            return null;
        }

        if (key.ExpiresAt.HasValue && key.ExpiresAt.Value < DateTime.UtcNow)
        {
            _logger.LogWarning("Expired API key for tenant {TenantId}", tenant.TenantId);
            return null;
        }

        // Update last used timestamp
        key.LastUsedAt = DateTime.UtcNow;
        tenant.LastActivityAt = DateTime.UtcNow;
        await _repository.UpdateAsync(tenant);

        return tenant;
    }

    public async Task UpdateUsageAsync(string tenantId, string metricName, int increment = 1)
    {
        var tenant = await _repository.GetByTenantIdAsync(tenantId);
        if (tenant == null)
        {
            throw new KeyNotFoundException($"Tenant {tenantId} not found");
        }

        // Reset metrics if new month
        if (tenant.Usage.LastResetDate.Month != DateTime.UtcNow.Month)
        {
            tenant.Usage = new UsageMetrics { LastResetDate = DateTime.UtcNow };
        }

        // Update specific metric
        switch (metricName.ToLower())
        {
            case "claims":
                tenant.Usage.ClaimsThisMonth += increment;
                break;
            case "priorauths":
                tenant.Usage.PriorAuthsThisMonth += increment;
                break;
            case "eligibility":
                tenant.Usage.EligibilityChecksThisMonth += increment;
                break;
            case "apicalls":
                tenant.Usage.ApiCallsThisMonth += increment;
                break;
        }

        await _repository.UpdateAsync(tenant);
    }

    public async Task<UsageMetrics> GetUsageAsync(string tenantId)
    {
        var tenant = await _repository.GetByTenantIdAsync(tenantId);
        if (tenant == null)
        {
            throw new KeyNotFoundException($"Tenant {tenantId} not found");
        }

        return tenant.Usage;
    }

    public async Task<OperatingModeConfiguration> GetOperatingModeAsync(string tenantId)
    {
        var tenant = await _repository.GetByTenantIdAsync(tenantId);
        if (tenant == null)
        {
            throw new KeyNotFoundException($"Tenant {tenantId} not found");
        }

        return tenant.OperatingMode ?? new OperatingModeConfiguration();
    }

    public async Task<OperatingModeConfiguration> UpdateOperatingModeAsync(string tenantId, UpdateOperatingModeRequest request)
    {
        var tenant = await _repository.GetByTenantIdAsync(tenantId);
        if (tenant == null)
        {
            throw new KeyNotFoundException($"Tenant {tenantId} not found");
        }

        // Validate engine names and modes
        var validModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "augment", "replace" };
        foreach (var (engine, mode) in request.Engines)
        {
            if (string.IsNullOrWhiteSpace(engine))
            {
                throw new InvalidOperationException("Engine name cannot be null or empty.");
            }

            if (string.IsNullOrWhiteSpace(mode) || !validModes.Contains(mode))
            {
                throw new InvalidOperationException(
                    $"Invalid operating mode '{mode ?? "null"}' for engine '{engine}'. Must be 'augment' or 'replace'.");
            }
        }

        tenant.OperatingMode ??= new OperatingModeConfiguration();

        // Merge: update specified engines, keep existing ones unchanged
        // Normalize engine keys with trim to prevent casing/whitespace duplicates
        foreach (var (engine, mode) in request.Engines)
        {
            tenant.OperatingMode.Engines[engine.Trim()] = mode.ToLowerInvariant();
        }

        tenant.OperatingMode.UpdatedAt = DateTime.UtcNow;

        await _repository.UpdateAsync(tenant);

        _logger.LogInformation(
            "Updated operating mode for tenant {TenantId}: {Engines}",
            SanitizeForLog(tenantId),
            string.Join(", ", tenant.OperatingMode.Engines.Select(e => $"{SanitizeForLog(e.Key)}={SanitizeForLog(e.Value)}")));

        return tenant.OperatingMode;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }

    // Helper methods
    private string GenerateTenantId(string organizationName)
    {
        // Convert to lowercase, remove special chars, add random suffix
        var cleanName = new string(organizationName
            .ToLower()
            .Where(c => char.IsLetterOrDigit(c) || c == '-')
            .ToArray())
            .Replace(" ", "-");

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        return $"{cleanName}-{suffix}";
    }

    private string GenerateApiKey()
    {
        // Generate cryptographically secure random API key
        var bytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }
        return $"cho_{Convert.ToBase64String(bytes).Replace("+", "").Replace("/", "").Replace("=", "")}";
    }

    private string HashApiKey(string apiKey)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(apiKey);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }
}
