using System.Text.Json.Serialization;

namespace CloudHealthOffice.TradingPartnerService.Models;

public class TradingPartner
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("tradingPartnerId")]
    public string TradingPartnerId { get; set; } = string.Empty;

    [JsonPropertyName("environment")]
    public string Environment { get; set; } = string.Empty;

    [JsonPropertyName("partnerName")]
    public string PartnerName { get; set; } = string.Empty;

    [JsonPropertyName("partnerType")]
    public string PartnerType { get; set; } = string.Empty;

    [JsonPropertyName("x12Config")]
    public X12Config? X12Config { get; set; }

    [JsonPropertyName("sftpConfig")]
    public SftpConfig? SftpConfig { get; set; }

    [JsonPropertyName("blobConfig")]
    public BlobConfig? BlobConfig { get; set; }

    [JsonPropertyName("transactionTypes")]
    public List<string> TransactionTypes { get; set; } = new();

    /// <summary>
    /// Billing-provider NPIs that route to this trading partner. Used by
    /// payment-service (5.10 batched 835 generation) to resolve which
    /// trading partner sends ERAs for a claim's billing provider. Empty
    /// list means "not the routing target for any specific NPI" — the
    /// trading partner is reached only through explicit
    /// (tenantId, tradingPartnerId, environment) lookup.
    /// </summary>
    [JsonPropertyName("billingProviderNpis")]
    public List<string> BillingProviderNpis { get; set; } = new();

    [JsonPropertyName("contactInfo")]
    public ContactInfo? ContactInfo { get; set; }

    [JsonPropertyName("businessRules")]
    public BusinessRules? BusinessRules { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "Active";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    /// <summary>Token subject of the user who created the record (never from a request body).</summary>
    [JsonPropertyName("createdBy")]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; set; }

    /// <summary>Token subject of the user who last changed the record (never from a request body).</summary>
    [JsonPropertyName("updatedBy")]
    public string? UpdatedBy { get; set; }

    [JsonPropertyName("lastTestedAt")]
    public DateTime? LastTestedAt { get; set; }

    /// <summary>Token subject of the caller who last started a connection test.</summary>
    [JsonPropertyName("lastTestedBy")]
    public string? LastTestedBy { get; set; }

    [JsonPropertyName("lastSuccessfulTransmission")]
    public DateTime? LastSuccessfulTransmission { get; set; }
}

public class X12Config
{
    [JsonPropertyName("senderId")]
    public string SenderId { get; set; } = string.Empty;

    [JsonPropertyName("receiverId")]
    public string ReceiverId { get; set; } = string.Empty;

    [JsonPropertyName("isaQualifier")]
    public string IsaQualifier { get; set; } = "ZZ";

    [JsonPropertyName("testIndicator")]
    public string TestIndicator { get; set; } = "P";
}

public class SftpConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; } = 22;

    [JsonPropertyName("paths")]
    public SftpPaths? Paths { get; set; }

    /// <summary>
    /// Name of the Key Vault secret (read through <c>ISecretProvider</c>) that holds the
    /// SFTP password. The password itself is never stored here and no response returns
    /// this reference (see <see cref="TradingPartnerSecrets"/>).
    /// </summary>
    [JsonPropertyName("passwordSecretRef")]
    public string? PasswordSecretRef { get; set; }

    /// <summary>
    /// Name of the Key Vault secret that holds the SFTP private key. Same rules as
    /// <see cref="PasswordSecretRef"/>.
    /// </summary>
    [JsonPropertyName("privateKeySecretRef")]
    public string? PrivateKeySecretRef { get; set; }
}

public class SftpPaths
{
    [JsonPropertyName("inbound")]
    public Dictionary<string, string> Inbound { get; set; } = new();

    [JsonPropertyName("outbound")]
    public Dictionary<string, string> Outbound { get; set; } = new();
}

public class BlobConfig
{
    [JsonPropertyName("containerName")]
    public string ContainerName { get; set; } = string.Empty;

    [JsonPropertyName("paths")]
    public Dictionary<string, string> Paths { get; set; } = new();

    [JsonPropertyName("retentionPolicies")]
    public Dictionary<string, int> RetentionPolicies { get; set; } = new();
}

public class ContactInfo
{
    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("phone")]
    public string Phone { get; set; } = string.Empty;

    [JsonPropertyName("technicalContact")]
    public string TechnicalContact { get; set; } = string.Empty;

    [JsonPropertyName("escalationEmail")]
    public string EscalationEmail { get; set; } = string.Empty;
}

public class BusinessRules
{
    [JsonPropertyName("maxFileSize")]
    public long MaxFileSize { get; set; }

    [JsonPropertyName("allowedFileTypes")]
    public List<string> AllowedFileTypes { get; set; } = new();

    [JsonPropertyName("pollingInterval")]
    public string PollingInterval { get; set; } = "PT5M";

    [JsonPropertyName("processingTimeout")]
    public string ProcessingTimeout { get; set; } = "PT10M";

    [JsonPropertyName("maxRetries")]
    public int MaxRetries { get; set; } = 3;

    [JsonPropertyName("retryBackoff")]
    public string RetryBackoff { get; set; } = "PT1M";
}
