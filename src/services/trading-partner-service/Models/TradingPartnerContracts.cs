using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CloudHealthOffice.TradingPartnerService.Models;

/// <summary>
/// Body of POST and PUT. The tenant, the record id, the creation/update stamps,
/// the acting user and the test/transmission timestamps are not part of it: the
/// tenant and the actor come from the CHO token, the rest from the stored record.
/// A body that still sends <c>tenantId</c>, <c>id</c>, <c>createdBy</c>,
/// <c>updatedBy</c> or similar has those fields ignored.
/// </summary>
public class TradingPartnerRequest
{
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

    [JsonPropertyName("billingProviderNpis")]
    public List<string> BillingProviderNpis { get; set; } = new();

    [JsonPropertyName("contactInfo")]
    public ContactInfo? ContactInfo { get; set; }

    [JsonPropertyName("businessRules")]
    public BusinessRules? BusinessRules { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

/// <summary>
/// What every read returns. It is built field by field from the stored record so
/// that nothing secret, today or added later, can reach a response by accident:
/// SFTP credentials appear only as <c>passwordConfigured</c> /
/// <c>privateKeyConfigured</c> flags, never as values or Key Vault references.
/// </summary>
public class TradingPartnerView
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("tenantId")] public string TenantId { get; init; } = string.Empty;
    [JsonPropertyName("tradingPartnerId")] public string TradingPartnerId { get; init; } = string.Empty;
    [JsonPropertyName("environment")] public string Environment { get; init; } = string.Empty;
    [JsonPropertyName("partnerName")] public string PartnerName { get; init; } = string.Empty;
    [JsonPropertyName("partnerType")] public string PartnerType { get; init; } = string.Empty;
    [JsonPropertyName("x12Config")] public X12Config? X12Config { get; init; }
    [JsonPropertyName("sftpConfig")] public SftpConfigView? SftpConfig { get; init; }
    [JsonPropertyName("blobConfig")] public BlobConfig? BlobConfig { get; init; }
    [JsonPropertyName("transactionTypes")] public List<string> TransactionTypes { get; init; } = new();
    [JsonPropertyName("billingProviderNpis")] public List<string> BillingProviderNpis { get; init; } = new();
    [JsonPropertyName("contactInfo")] public ContactInfo? ContactInfo { get; init; }
    [JsonPropertyName("businessRules")] public BusinessRules? BusinessRules { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; init; }
    [JsonPropertyName("createdBy")] public string? CreatedBy { get; init; }
    [JsonPropertyName("updatedAt")] public DateTime? UpdatedAt { get; init; }
    [JsonPropertyName("updatedBy")] public string? UpdatedBy { get; init; }
    [JsonPropertyName("lastTestedAt")] public DateTime? LastTestedAt { get; init; }
    [JsonPropertyName("lastSuccessfulTransmission")] public DateTime? LastSuccessfulTransmission { get; init; }

    public static TradingPartnerView From(TradingPartner p) => new()
    {
        Id = p.Id,
        TenantId = p.TenantId,
        TradingPartnerId = p.TradingPartnerId,
        Environment = p.Environment,
        PartnerName = p.PartnerName,
        PartnerType = p.PartnerType,
        X12Config = p.X12Config,
        SftpConfig = SftpConfigView.From(p.SftpConfig),
        BlobConfig = p.BlobConfig,
        TransactionTypes = p.TransactionTypes,
        BillingProviderNpis = p.BillingProviderNpis,
        ContactInfo = p.ContactInfo,
        BusinessRules = p.BusinessRules,
        Status = p.Status,
        CreatedAt = p.CreatedAt,
        CreatedBy = p.CreatedBy,
        UpdatedAt = p.UpdatedAt,
        UpdatedBy = p.UpdatedBy,
        LastTestedAt = p.LastTestedAt,
        LastSuccessfulTransmission = p.LastSuccessfulTransmission
    };
}

public class SftpConfigView
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("username")] public string Username { get; init; } = string.Empty;
    [JsonPropertyName("host")] public string Host { get; init; } = string.Empty;
    [JsonPropertyName("port")] public int Port { get; init; }
    [JsonPropertyName("paths")] public SftpPaths? Paths { get; init; }
    [JsonPropertyName("passwordConfigured")] public bool PasswordConfigured { get; init; }
    [JsonPropertyName("privateKeyConfigured")] public bool PrivateKeyConfigured { get; init; }

    public static SftpConfigView? From(SftpConfig? c) => c is null ? null : new()
    {
        Enabled = c.Enabled,
        Username = c.Username,
        Host = c.Host,
        Port = c.Port,
        Paths = c.Paths,
        PasswordConfigured = !string.IsNullOrEmpty(c.PasswordSecretRef),
        PrivateKeyConfigured = !string.IsNullOrEmpty(c.PrivateKeySecretRef)
    };
}

/// <summary>
/// The NPI lookup payment-service makes while building 835 envelopes: only the
/// identifiers it needs for ISA/GS routing (the fields its
/// <c>TradingPartnerSummary</c> reads, plus type and status). No SFTP, blob or
/// contact details.
/// </summary>
public class TradingPartnerRoutingView
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("tenantId")] public string TenantId { get; init; } = string.Empty;
    [JsonPropertyName("tradingPartnerId")] public string TradingPartnerId { get; init; } = string.Empty;
    [JsonPropertyName("environment")] public string Environment { get; init; } = string.Empty;
    [JsonPropertyName("partnerName")] public string PartnerName { get; init; } = string.Empty;
    [JsonPropertyName("partnerType")] public string PartnerType { get; init; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("x12Config")] public X12Config? X12Config { get; init; }
    [JsonPropertyName("billingProviderNpis")] public List<string> BillingProviderNpis { get; init; } = new();

    public static TradingPartnerRoutingView From(TradingPartner p) => new()
    {
        Id = p.Id,
        TenantId = p.TenantId,
        TradingPartnerId = p.TradingPartnerId,
        Environment = p.Environment,
        PartnerName = p.PartnerName,
        PartnerType = p.PartnerType,
        Status = p.Status,
        X12Config = p.X12Config,
        BillingProviderNpis = p.BillingProviderNpis
    };
}

/// <summary>
/// Rules for trading-partner credentials (SFTP passwords and private keys, API
/// keys, AS2 private keys and similar).
/// <list type="bullet">
/// <item>A literal credential in a request body is refused (400). It is never
/// stored, so it can never be read back.</item>
/// <item>A credential is stored only as the name of a Key Vault secret
/// (resolved with <c>ISecretProvider</c>), in <c>passwordSecretRef</c> /
/// <c>privateKeySecretRef</c>. The name must start with
/// <c>tp--{tenantId}--</c> for the caller's token tenant, so a tenant can only
/// point at its own secrets, never at another tenant's or at a platform secret
/// such as a database key.</item>
/// <item>No response returns a credential or its reference.</item>
/// </list>
/// </summary>
public static class TradingPartnerSecrets
{
    // Property names that carry a credential value. Compared after lower-casing and
    // removing '-' and '_'.
    private static readonly string[] CredentialMarkers =
        ["password", "passwd", "passphrase", "privatekey", "secret", "apikey", "token", "pfx", "pkcs12"];

    private static readonly HashSet<string> AllowedReferences =
        new(StringComparer.Ordinal) { "passwordsecretref", "privatekeysecretref" };

    private static readonly Regex SecretNameChars = new("^[0-9A-Za-z-]+$", RegexOptions.Compiled);

    /// <summary>
    /// Returns the JSON path of the first property that looks like a literal
    /// credential, or null when the body carries none.
    /// </summary>
    public static string? FindLiteralCredential(JsonElement element, string path = "$")
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var childPath = $"{path}.{property.Name}";
                    var normalized = property.Name.Replace("-", "").Replace("_", "").ToLowerInvariant();
                    if (!AllowedReferences.Contains(normalized)
                        && CredentialMarkers.Any(normalized.Contains))
                    {
                        return childPath;
                    }

                    if (FindLiteralCredential(property.Value, childPath) is { } found)
                        return found;
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (FindLiteralCredential(item, $"{path}[{index++}]") is { } found)
                        return found;
                }
                break;
        }

        return null;
    }

    /// <summary>The prefix every Key Vault reference for this tenant must carry.</summary>
    public static string ReferencePrefix(string tenantId) => $"tp--{tenantId}--";

    /// <summary>
    /// Null when the reference is acceptable for the tenant (or absent), else the reason.
    /// </summary>
    public static string? ValidateReference(string? reference, string tenantId, string field)
    {
        if (string.IsNullOrEmpty(reference))
            return null;

        var prefix = ReferencePrefix(tenantId);
        if (reference.Length > 127
            || !SecretNameChars.IsMatch(reference)
            || !reference.StartsWith(prefix, StringComparison.Ordinal)
            || reference.Length == prefix.Length)
        {
            return $"{field} must be the name of a Key Vault secret starting with '{prefix}' " +
                   "(letters, digits and '-', at most 127 characters). Store the credential in Key Vault " +
                   "and send only its name.";
        }

        return null;
    }
}
