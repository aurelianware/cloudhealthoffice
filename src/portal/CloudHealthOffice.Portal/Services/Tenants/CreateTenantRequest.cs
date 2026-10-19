using System.ComponentModel.DataAnnotations;

namespace CloudHealthOffice.Portal.Services;

public class CreateTenantRequest
{
    [Required]
    [StringLength(300)]
    public string OrganizationName { get; set; } = string.Empty;

    [Required]
    public string AzureTenantId { get; set; } = string.Empty;

    [Required]
    public string Tier { get; set; } = "starter";

    public string TenantDisplayName { get; set; } = string.Empty;

    public string SubscriptionStatus { get; set; } = "Trial";

    public string AdminEmail { get; set; } = string.Empty;

    public List<string> AdminEmails { get; set; } = new();

    public bool IsDemo { get; set; }

    public string? Notes { get; set; }

    public string? StripePaymentMethodId { get; set; }
    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public List<string> EnabledModules { get; set; } = new();
}
