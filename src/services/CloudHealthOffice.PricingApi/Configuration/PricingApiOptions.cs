namespace CloudHealthOffice.PricingApi.Configuration;

public class PricingApiOptions
{
    public const string SectionName = "PricingApi";

    public string MongoConnectionString { get; set; } = "mongodb://localhost:27017";
    public string DatabaseName { get; set; } = "cho_pricing";
    public string MedicareFeeSchedulePath { get; set; } = "./Data/FeeSchedules";
    public int FreeTierMonthlyLimit { get; set; } = 1_000;
    public int StarterTierMonthlyLimit { get; set; } = 10_000;
    public int ProfessionalTierMonthlyLimit { get; set; } = 100_000;
}

public class RateLimitOptions
{
    public const string SectionName = "RateLimiting";

    public int PermitLimit { get; set; } = 100;
    public int WindowSeconds { get; set; } = 60;
    public int QueueLimit { get; set; } = 10;
}
