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

    /// <summary>
    /// Where premium-billing-service and capitation-service send NACHA files:
    /// the tenant's bank SFTP drop. Null when not configured (files are then
    /// held encrypted for a platform admin's retrieval).
    /// </summary>
    [JsonPropertyName("nachaTransmission")]
    public NachaTransmissionConfig? NachaTransmission { get; set; }
}

/// <summary>
/// The tenant's bank SFTP drop for NACHA files. Holds no credential: only the
/// names of Key Vault secrets (<c>nacha--{tenantId}--...</c>), which the
/// sending service reads with its managed identity. A body that carries a
/// credential itself is refused (400). Readers without <c>settings:manage</c>
/// see only whether each credential is configured, not its secret name.
/// </summary>
public class NachaTransmissionConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("host")]
    public string? Host { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; } = 22;

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>Key Vault secret name of the SSH private key.</summary>
    [JsonPropertyName("privateKeySecretRef")]
    public string? PrivateKeySecretRef { get; set; }

    /// <summary>Key Vault secret name of the key's passphrase, or of the password when there is no key.</summary>
    [JsonPropertyName("passwordSecretRef")]
    public string? PasswordSecretRef { get; set; }

    /// <summary>The bank's SSH host key, pinned: <c>SHA256:&lt;base64&gt;</c>. Required to enable.</summary>
    [JsonPropertyName("hostKeyFingerprint")]
    public string? HostKeyFingerprint { get; set; }

    [JsonPropertyName("remoteDirectory")]
    public string? RemoteDirectory { get; set; }

    /// <summary>Read-only: whether a private key secret is configured.</summary>
    [JsonPropertyName("privateKeyConfigured")]
    public bool PrivateKeyConfigured => MaskedPrivateKey || !string.IsNullOrEmpty(PrivateKeySecretRef);

    /// <summary>Read-only: whether a password secret is configured.</summary>
    [JsonPropertyName("passwordConfigured")]
    public bool PasswordConfigured => MaskedPassword || !string.IsNullOrEmpty(PasswordSecretRef);

    /// <summary>
    /// Anything else the body carried (for example a literal <c>password</c> or
    /// <c>privateKey</c>). Refused when present; never stored.
    /// </summary>
    [JsonExtensionData]
    [MongoDB.Bson.Serialization.Attributes.BsonIgnore]
    public Dictionary<string, System.Text.Json.JsonElement>? UnknownProperties { get; set; }

    /// <summary>The same settings without secret names (for readers without settings:manage).</summary>
    public NachaTransmissionConfig WithoutSecretNames() => new()
    {
        Enabled = Enabled,
        Host = Host,
        Port = Port,
        Username = Username,
        HostKeyFingerprint = HostKeyFingerprint,
        RemoteDirectory = RemoteDirectory,
        PrivateKeySecretRef = null,
        PasswordSecretRef = null,
        MaskedPrivateKey = PrivateKeyConfigured,
        MaskedPassword = PasswordConfigured,
    };

    // Carry the "configured" flags through WithoutSecretNames.
    [System.Text.Json.Serialization.JsonIgnore]
    [MongoDB.Bson.Serialization.Attributes.BsonIgnore]
    internal bool MaskedPrivateKey { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    [MongoDB.Bson.Serialization.Attributes.BsonIgnore]
    internal bool MaskedPassword { get; init; }

    /// <summary>
    /// Whether two settings transmit the same way (destination, host key,
    /// credentials and directory). Null equals only null.
    /// </summary>
    public static bool SameSettings(NachaTransmissionConfig? a, NachaTransmissionConfig? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return a.Enabled == b.Enabled
               && a.Port == b.Port
               && string.Equals(a.Host, b.Host, StringComparison.Ordinal)
               && string.Equals(a.Username, b.Username, StringComparison.Ordinal)
               && string.Equals(a.PrivateKeySecretRef, b.PrivateKeySecretRef, StringComparison.Ordinal)
               && string.Equals(a.PasswordSecretRef, b.PasswordSecretRef, StringComparison.Ordinal)
               && string.Equals(a.HostKeyFingerprint, b.HostKeyFingerprint, StringComparison.Ordinal)
               && string.Equals(a.RemoteDirectory, b.RemoteDirectory, StringComparison.Ordinal);
    }

    private static readonly System.Text.RegularExpressions.Regex SecretNameChars = new("^[0-9A-Za-z-]+$");

    /// <summary>Key Vault secret names for a tenant's NACHA credentials must start with this.</summary>
    public static string SecretPrefix(string tenantId) => $"nacha--{tenantId}--";

    /// <summary>Null when the settings may be stored for <paramref name="tenantId"/>, else why not.</summary>
    public string? Validate(string tenantId)
    {
        if (UnknownProperties is { Count: > 0 })
            return $"paymentControls.nachaTransmission does not accept '{UnknownProperties.Keys.First()}'. Credentials are never " +
                   "stored here: put them in Key Vault and give the secret names in privateKeySecretRef / passwordSecretRef.";
        foreach (var (field, name) in new[] { ("privateKeySecretRef", PrivateKeySecretRef), ("passwordSecretRef", PasswordSecretRef) })
        {
            if (string.IsNullOrEmpty(name)) continue;
            var prefix = SecretPrefix(tenantId);
            if (name.Length > 127 || !SecretNameChars.IsMatch(name) || !name.StartsWith(prefix, StringComparison.Ordinal) || name.Length == prefix.Length)
                return $"paymentControls.nachaTransmission.{field} must be a Key Vault secret name (letters, digits and '-', " +
                       $"at most 127) starting with '{prefix}'.";
        }
        if (Port is < 1 or > 65535)
            return "paymentControls.nachaTransmission.port is out of range.";
        if (!string.IsNullOrEmpty(HostKeyFingerprint) && !IsSha256Fingerprint(HostKeyFingerprint))
            return "paymentControls.nachaTransmission.hostKeyFingerprint must be 'SHA256:<base64>' (ssh-keygen -lf -E sha256).";
        if (RemoteDirectory?.Contains("..", StringComparison.Ordinal) == true)
            return "paymentControls.nachaTransmission.remoteDirectory may not contain '..'.";
        if (!Enabled)
            return null;
        if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(RemoteDirectory))
            return "Enabling paymentControls.nachaTransmission needs host, username and remoteDirectory.";
        if (string.IsNullOrEmpty(PrivateKeySecretRef) && string.IsNullOrEmpty(PasswordSecretRef))
            return "Enabling paymentControls.nachaTransmission needs privateKeySecretRef or passwordSecretRef.";
        if (string.IsNullOrWhiteSpace(HostKeyFingerprint))
            return "Enabling paymentControls.nachaTransmission needs hostKeyFingerprint: the bank's SSH host key is pinned.";
        return null;
    }

    private static bool IsSha256Fingerprint(string text)
    {
        if (!text.StartsWith("SHA256:", StringComparison.Ordinal)) return false;
        var b64 = text["SHA256:".Length..].TrimEnd('=');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        try { return Convert.FromBase64String(b64).Length == 32; }
        catch (FormatException) { return false; }
    }
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
