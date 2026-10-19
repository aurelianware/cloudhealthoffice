using System.ComponentModel.DataAnnotations;

namespace CloudHealthOffice.Portal.Services;

public class CreatePaymentRunRequest
{
    public string RunName { get; set; } = string.Empty;
    [Required]
    public string LineOfBusiness { get; set; } = string.Empty;
    public DateTime ClaimServiceDateFrom { get; set; }
    public DateTime ClaimServiceDateTo { get; set; }
    public string? SponsorId { get; set; }
    public string? PlanId { get; set; }
    public List<string> ClaimStatuses { get; set; } = new() { "Approved" };
}
