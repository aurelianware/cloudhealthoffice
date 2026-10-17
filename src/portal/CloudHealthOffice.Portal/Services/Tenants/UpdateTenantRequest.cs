using System.ComponentModel.DataAnnotations;

namespace CloudHealthOffice.Portal.Services;

public class UpdateTenantRequest
{
    [StringLength(300)]
    public string? OrganizationName { get; set; }

    public string? Tier { get; set; }

    public string? SubscriptionStatus { get; set; }

    public List<string>? AdminEmails { get; set; }

    public bool? IsDemo { get; set; }

    public string? Notes { get; set; }
}
