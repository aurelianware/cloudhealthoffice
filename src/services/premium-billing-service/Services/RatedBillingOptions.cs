using PremiumBillingService.Models;

namespace PremiumBillingService.Services;

/// <summary>
/// Per-tenant switch for rated billing (configuration section
/// <c>PremiumBilling:RatedBilling</c>). A tenant that is not listed, or listed
/// with <c>Enabled: false</c>, keeps the original behaviour: invoice lines
/// priced from coverage-service's stored premium.
/// <code>
/// "PremiumBilling": { "RatedBilling": { "Tenants": {
///   "tenant-a": { "Enabled": true, "BillFormat": "ListBill", "MaxRetroMonths": 3,
///                 "Groups": { "GRP-100": { "BillFormat": "Composite", "EmployerContributionPercent": 75 } } } } } }
/// </code>
/// </summary>
public sealed class RatedBillingOptions
{
    public const string SectionName = "PremiumBilling:RatedBilling";

    public Dictionary<string, RatedBillingTenantOptions> Tenants { get; set; } = new();

    private static readonly RatedBillingTenantOptions Off = new() { Enabled = false };

    /// <summary>The tenant's settings; disabled when the tenant is not configured.</summary>
    public RatedBillingTenantOptions For(string? tenantId)
    {
        if (string.IsNullOrEmpty(tenantId))
            return Off;
        foreach (var (key, value) in Tenants)
        {
            if (string.Equals(key, tenantId, StringComparison.OrdinalIgnoreCase))
                return value;
        }
        return Off;
    }

    /// <summary>Every problem with the settings (checked at startup).</summary>
    public IEnumerable<string> Problems()
    {
        foreach (var (tenant, options) in Tenants)
        {
            if (options.MaxRetroMonths is < 0 or > 24)
                yield return $"{SectionName}:Tenants:{tenant}:MaxRetroMonths must be 0–24";
            if (options.EmployerContributionPercent is < 0 or > 100)
                yield return $"{SectionName}:Tenants:{tenant}:EmployerContributionPercent must be 0–100";
            foreach (var (group, groupOptions) in options.Groups)
            {
                if (groupOptions.EmployerContributionPercent is < 0 or > 100)
                    yield return $"{SectionName}:Tenants:{tenant}:Groups:{group}:EmployerContributionPercent must be 0–100";
            }
        }
    }
}

public sealed class RatedBillingTenantOptions
{
    /// <summary>Price invoices with the rating engine. Default false: the original behaviour.</summary>
    public bool Enabled { get; set; }

    /// <summary>How many months back retro adds, terms and rate corrections are reconciled.</summary>
    public int MaxRetroMonths { get; set; } = 3;

    public BillFormat BillFormat { get; set; } = BillFormat.ListBill;

    /// <summary>Employer share of each line (0–100), for display; the invoice total is the full premium.</summary>
    public decimal EmployerContributionPercent { get; set; }

    /// <summary>Keep every rated invoice in Draft until someone issues it (POST /premium-invoices/{id}/issue).</summary>
    public bool HoldForReview { get; set; }

    public Dictionary<string, RatedBillingGroupOptions> Groups { get; set; } = new();

    public RatedBillingGroupOptions? ForGroup(string groupNumber)
    {
        foreach (var (key, value) in Groups)
        {
            if (string.Equals(key, groupNumber, StringComparison.OrdinalIgnoreCase))
                return value;
        }
        return null;
    }

    public BillFormat BillFormatFor(string groupNumber) => ForGroup(groupNumber)?.BillFormat ?? BillFormat;

    public decimal EmployerContributionPercentFor(string groupNumber) =>
        ForGroup(groupNumber)?.EmployerContributionPercent ?? EmployerContributionPercent;
}

public sealed class RatedBillingGroupOptions
{
    public BillFormat? BillFormat { get; set; }
    public decimal? EmployerContributionPercent { get; set; }
}
