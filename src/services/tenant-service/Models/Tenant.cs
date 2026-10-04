using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using CloudHealthOffice.OperatingMode;

namespace TenantService.Models;

/// <summary>
/// Represents a health plan tenant in the multi-tenant SaaS platform
/// Each tenant is a separate payer/health plan with isolated data
/// </summary>
public class Tenant
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("tenantId")]
    [Required]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("tenantName")]
    [Required]
    public string TenantName { get; set; } = string.Empty;

    [JsonPropertyName("organizationName")]
    [Required]
    public string OrganizationName { get; set; } = string.Empty;

    [JsonPropertyName("subscriptionTier")]
    public string SubscriptionTier { get; set; } = "starter"; // starter, professional, enterprise

    [JsonPropertyName("status")]
    public string Status { get; set; } = "pending"; // pending, active, suspended, terminated

    [JsonPropertyName("contactInfo")]
    public ContactInfo ContactInfo { get; set; } = new();

    [JsonPropertyName("apiKeys")]
    public List<ApiKey> ApiKeys { get; set; } = new();

    [JsonPropertyName("configuration")]
    public TenantConfiguration Configuration { get; set; } = new();

    [JsonPropertyName("billing")]
    public BillingInfo? Billing { get; set; }

    [JsonPropertyName("operatingMode")]
    public OperatingModeConfiguration? OperatingMode { get; set; }

    [JsonPropertyName("usage")]
    public UsageMetrics Usage { get; set; } = new();

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Token subject of the caller that created the tenant. Never read from a request body.</summary>
    [JsonPropertyName("createdBy")]
    public string? CreatedBy { get; set; }

    /// <summary>Token subject of the caller that last changed the tenant. Never read from a request body.</summary>
    [JsonPropertyName("updatedBy")]
    public string? UpdatedBy { get; set; }

    [JsonPropertyName("activatedAt")]
    public DateTime? ActivatedAt { get; set; }

    [JsonPropertyName("lastActivityAt")]
    public DateTime? LastActivityAt { get; set; }
}

public class ContactInfo
{
    [JsonPropertyName("primaryContact")]
    public string PrimaryContact { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("phone")]
    public string Phone { get; set; } = string.Empty;

    [JsonPropertyName("supportEmail")]
    [EmailAddress]
    public string? SupportEmail { get; set; }

    [JsonPropertyName("address")]
    public Address? Address { get; set; }
}

public class Address
{
    [JsonPropertyName("street")]
    public string Street { get; set; } = string.Empty;

    [JsonPropertyName("city")]
    public string City { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("zipCode")]
    public string ZipCode { get; set; } = string.Empty;

    [JsonPropertyName("country")]
    public string Country { get; set; } = "USA";
}

public class ApiKey
{
    [JsonPropertyName("keyId")]
    public string KeyId { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 of the key; the key itself is never stored. Kept out of every
    /// API response: only the prefix identifies a key to a reader.
    /// </summary>
    [JsonPropertyName("keyHash")]
    [JsonIgnore]
    public string KeyHash { get; set; } = string.Empty;

    [JsonPropertyName("keyPrefix")]
    public string KeyPrefix { get; set; } = string.Empty; // First 8 chars for identification

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("expiresAt")]
    public DateTime? ExpiresAt { get; set; }

    [JsonPropertyName("lastUsedAt")]
    public DateTime? LastUsedAt { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; } = true;

    [JsonPropertyName("scopes")]
    public List<string> Scopes { get; set; } = new(); // e.g., "claims:read", "claims:write"

    /// <summary>Token subject of the caller that issued the key.</summary>
    [JsonPropertyName("createdBy")]
    public string? CreatedBy { get; set; }

    /// <summary>Token subject of the caller that revoked the key.</summary>
    [JsonPropertyName("revokedBy")]
    public string? RevokedBy { get; set; }

    [JsonPropertyName("revokedAt")]
    public DateTime? RevokedAt { get; set; }
}

public class TenantConfiguration
{
    [JsonPropertyName("enabledModules")]
    public List<string> EnabledModules { get; set; } = new(); // attachments, authorizations, appeals, ecs

    [JsonPropertyName("azureRegion")]
    public string AzureRegion { get; set; } = "eastus";

    [JsonPropertyName("environment")]
    public string Environment { get; set; } = "production"; // dev, uat, production

    [JsonPropertyName("sftpProvisioned")]
    public bool SftpProvisioned { get; set; } = false;

    [JsonPropertyName("sftpEnvironments")]
    public List<string> SftpEnvironments { get; set; } = new(); // prod, preprod, dev

    [JsonPropertyName("clearinghouseConfig")]
    public ClearinghouseConfig? Clearinghouse { get; set; }

    [JsonPropertyName("eligibilityPlatform")]
    public EligibilityConfig? EligibilityPlatform { get; set; }

    [JsonPropertyName("benefitPlanPlatform")]
    public BenefitPlanConfig? BenefitPlanPlatform { get; set; }

    /// <summary>Provider-directory platform, read by provider-service.</summary>
    [JsonPropertyName("providerPlatform")]
    public ProviderPlatformConfig? ProviderPlatform { get; set; }

    /// <summary>Claims platform, read by claims-service.</summary>
    [JsonPropertyName("claimsPlatform")]
    public ClaimsPlatformConfig? ClaimsPlatform { get; set; }

    /// <summary>ID card platform, read by idcard-service.</summary>
    [JsonPropertyName("idCardPlatform")]
    public IdCardPlatformConfig? IdCardPlatform { get; set; }

    [JsonPropertyName("customSettings")]
    public Dictionary<string, string> CustomSettings { get; set; } = new();

    [JsonPropertyName("paymentControls")]
    public PaymentControlsConfig PaymentControls { get; set; } = new();

    [JsonPropertyName("personalRepresentativeControls")]
    public PersonalRepresentativeControlsConfig PersonalRepresentativeControls { get; set; } = new();
}

/// <summary>
/// Personal representative activation controls, read by
/// personal-representative-service.
/// </summary>
public class PersonalRepresentativeControlsConfig
{
    /// <summary>
    /// A user who established a personal representative cannot activate it;
    /// another user must. On by default; set to false only for very small
    /// tenants without a second reviewer. Every same-user activation this
    /// allows is logged as an audit warning.
    /// </summary>
    [JsonPropertyName("requireSecondPerson")]
    public bool RequireSecondPerson { get; set; } = true;
}

/// <summary>
/// Payment approval controls, read by capitation-service.
/// </summary>
public class PaymentControlsConfig
{
    /// <summary>
    /// Maker-checker: a user who prepared a payment (created or executed its
    /// capitation run) cannot approve or release it. On by default; set to
    /// false only for very small tenants without a second approver. Every
    /// same-user approval or release this allows is logged as an audit warning.
    /// </summary>
    [JsonPropertyName("enforceSeparationOfDuties")]
    public bool EnforceSeparationOfDuties { get; set; } = true;
}

public class ClearinghouseConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty; // Availity, Change Healthcare, Optum

    [JsonPropertyName("senderId")]
    public string SenderId { get; set; } = string.Empty;

    [JsonPropertyName("receiverId")]
    public string ReceiverId { get; set; } = string.Empty;

    [JsonPropertyName("sftpHost")]
    public string SftpHost { get; set; } = string.Empty;

    [JsonPropertyName("sftpUsername")]
    public string SftpUsername { get; set; } = string.Empty;
}

/// <summary>
/// Configuration for tenant's eligibility verification platform.
/// Controls which adapter is used at runtime during eligibility checks.
/// </summary>
public class EligibilityConfig
{
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "cho"; // cho, availity, change-healthcare, waystar, custom

    [JsonPropertyName("apiEndpoint")]
    public string? ApiEndpoint { get; set; }

    [JsonPropertyName("keyVaultSecretName")]
    public string? KeyVaultSecretName { get; set; }

    [JsonPropertyName("timeoutMs")]
    public int TimeoutMs { get; set; } = 5000;

    [JsonPropertyName("retryCount")]
    public int RetryCount { get; set; } = 2;

    [JsonPropertyName("platformSettings")]
    public Dictionary<string, string> PlatformSettings { get; set; } = new();
}

/// <summary>
/// Configuration for tenant's benefit-plan source-of-truth platform.
/// Controls which adapter is used at runtime when reading plans.
/// Mirrors <see cref="EligibilityConfig"/>.
/// </summary>
public class BenefitPlanConfig
{
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "cho"; // cho, qnxt, facets, healthedge

    [JsonPropertyName("apiEndpoint")]
    public string? ApiEndpoint { get; set; }

    [JsonPropertyName("keyVaultSecretName")]
    public string? KeyVaultSecretName { get; set; }

    [JsonPropertyName("timeoutMs")]
    public int TimeoutMs { get; set; } = 5000;

    [JsonPropertyName("retryCount")]
    public int RetryCount { get; set; } = 2;

    [JsonPropertyName("platformSettings")]
    public Dictionary<string, string> PlatformSettings { get; set; } = new();
}

/// <summary>
/// Configuration for tenant's provider-directory platform. Controls which
/// adapter provider-service uses at runtime when reading providers and
/// networks. Mirrors <see cref="BenefitPlanConfig"/>.
/// </summary>
public class ProviderPlatformConfig
{
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "cho"; // cho, qnxt, facets, healthedge

    [JsonPropertyName("apiEndpoint")]
    public string? ApiEndpoint { get; set; }

    [JsonPropertyName("keyVaultSecretName")]
    public string? KeyVaultSecretName { get; set; }

    [JsonPropertyName("timeoutMs")]
    public int TimeoutMs { get; set; } = 5000;

    [JsonPropertyName("retryCount")]
    public int RetryCount { get; set; } = 2;

    [JsonPropertyName("platformSettings")]
    public Dictionary<string, string> PlatformSettings { get; set; } = new();
}

/// <summary>
/// Configuration for tenant's claims platform. Controls which adapter
/// claims-service uses at runtime. Mirrors <see cref="BenefitPlanConfig"/>.
/// </summary>
public class ClaimsPlatformConfig
{
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "cho"; // cho, qnxt, facets

    [JsonPropertyName("apiEndpoint")]
    public string? ApiEndpoint { get; set; }

    [JsonPropertyName("keyVaultSecretName")]
    public string? KeyVaultSecretName { get; set; }

    [JsonPropertyName("timeoutMs")]
    public int TimeoutMs { get; set; } = 5000;

    [JsonPropertyName("retryCount")]
    public int RetryCount { get; set; } = 2;

    [JsonPropertyName("platformSettings")]
    public Dictionary<string, string> PlatformSettings { get; set; } = new();
}

/// <summary>
/// Configuration for tenant's ID card platform. Controls which adapter
/// idcard-service uses at runtime. Mirrors <see cref="BenefitPlanConfig"/>.
/// </summary>
public class IdCardPlatformConfig
{
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "cho"; // cho, qnxt, vendor

    [JsonPropertyName("apiEndpoint")]
    public string? ApiEndpoint { get; set; }

    [JsonPropertyName("keyVaultSecretName")]
    public string? KeyVaultSecretName { get; set; }

    [JsonPropertyName("timeoutMs")]
    public int TimeoutMs { get; set; } = 5000;

    [JsonPropertyName("retryCount")]
    public int RetryCount { get; set; } = 2;

    [JsonPropertyName("platformSettings")]
    public Dictionary<string, string> PlatformSettings { get; set; } = new();
}

public class BillingInfo
{
    [JsonPropertyName("stripeCustomerId")]
    public string? StripeCustomerId { get; set; }

    [JsonPropertyName("stripeSubscriptionId")]
    public string? StripeSubscriptionId { get; set; }

    [JsonPropertyName("billingEmail")]
    public string BillingEmail { get; set; } = string.Empty;

    [JsonPropertyName("paymentMethod")]
    public string? PaymentMethod { get; set; } // card, ach, invoice

    [JsonPropertyName("billingCycle")]
    public string BillingCycle { get; set; } = "monthly"; // monthly, annual

    [JsonPropertyName("nextBillingDate")]
    public DateTime? NextBillingDate { get; set; }

    [JsonPropertyName("currentPeriodStart")]
    public DateTime? CurrentPeriodStart { get; set; }

    [JsonPropertyName("currentPeriodEnd")]
    public DateTime? CurrentPeriodEnd { get; set; }
}

public class UsageMetrics
{
    [JsonPropertyName("claimsThisMonth")]
    public int ClaimsThisMonth { get; set; }

    [JsonPropertyName("priorAuthsThisMonth")]
    public int PriorAuthsThisMonth { get; set; }

    [JsonPropertyName("eligibilityChecksThisMonth")]
    public int EligibilityChecksThisMonth { get; set; }

    [JsonPropertyName("apiCallsThisMonth")]
    public int ApiCallsThisMonth { get; set; }

    [JsonPropertyName("storageUsedGB")]
    public decimal StorageUsedGB { get; set; }

    [JsonPropertyName("lastResetDate")]
    public DateTime LastResetDate { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// DTO for creating a new tenant
/// </summary>
public class CreateTenantRequest
{
    [Required]
    public string TenantName { get; set; } = string.Empty;

    [Required]
    public string OrganizationName { get; set; } = string.Empty;

    [Required]
    public string SubscriptionTier { get; set; } = "starter";

    [Required]
    public ContactInfo ContactInfo { get; set; } = new();

    public List<string>? EnabledModules { get; set; }

    public List<string>? Environments { get; set; } // prod, preprod, dev

    public ClearinghouseConfig? Clearinghouse { get; set; }

    public EligibilityConfig? EligibilityPlatform { get; set; }

    public BenefitPlanConfig? BenefitPlanPlatform { get; set; }

    public ProviderPlatformConfig? ProviderPlatform { get; set; }

    public ClaimsPlatformConfig? ClaimsPlatform { get; set; }

    public IdCardPlatformConfig? IdCardPlatform { get; set; }
}

/// <summary>
/// DTO for updating tenant
/// </summary>
public class UpdateTenantRequest
{
    public string? TenantName { get; set; }
    public string? OrganizationName { get; set; }
    public string? SubscriptionTier { get; set; }
    public string? Status { get; set; }
    public ContactInfo? ContactInfo { get; set; }
    public TenantConfiguration? Configuration { get; set; }
}

/// <summary>
/// DTO for API key creation
/// </summary>
public class CreateApiKeyRequest
{
    [Required]
    public string Name { get; set; } = string.Empty;

    public DateTime? ExpiresAt { get; set; }

    public List<string> Scopes { get; set; } = new();
}

/// <summary>
/// DTO for API key response (includes plain-text key, shown only once)
/// </summary>
public class ApiKeyResponse
{
    public string KeyId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty; // Plain text, shown only on creation
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public List<string> Scopes { get; set; } = new();
}

/// <summary>
/// DTO for updating a tenant's operating mode configuration.
/// </summary>
public class UpdateOperatingModeRequest
{
    /// <summary>
    /// Engine operating modes. Keys are engine names (e.g., "benefitCalculation"),
    /// values are "augment" or "replace".
    /// </summary>
    public Dictionary<string, string> Engines { get; set; } = new();
}
