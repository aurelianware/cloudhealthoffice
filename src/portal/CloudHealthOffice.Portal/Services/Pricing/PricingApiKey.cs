namespace CloudHealthOffice.Portal.Services;

public class PricingApiKey
{
    /// <summary>
    /// The key itself: set only on the response that created it (the Pricing
    /// API stores a hash and shows the key once). Empty in listings.
    /// </summary>
    public string ApiKey { get; set; } = "";

    /// <summary>The key's public id; deactivation addresses the key by it.</summary>
    public string KeyId { get; set; } = "";

    /// <summary>The first characters of the key, for identification.</summary>
    public string KeyPrefix { get; set; } = "";

    public string TenantName { get; set; } = "";
    public string ContactEmail { get; set; } = "";
    public string Tier { get; set; } = "";
    public int MonthlyLimit { get; set; }
    public int CurrentMonthUsage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsActive { get; set; }
}
