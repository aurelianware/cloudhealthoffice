using System.Text.Json.Serialization;

namespace CloudHealthOffice.Portal.Services;

public class Coverage
{
    public string? Id { get; set; }
    public string? CoverageId { get; set; }
    public string? MemberId { get; set; }
    public string? PlanId { get; set; }
    public string? PlanName { get; set; }
    public string? GroupNumber { get; set; }
    public string? CoverageLevel { get; set; }
    public string? InsuranceLineCode { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    [System.Text.Json.Serialization.JsonConverter(typeof(CoverageStatusValueConverter))]
    public int Status { get; set; }

    [System.Text.Json.Serialization.JsonConverter(typeof(CoverageLineOfBusinessValueConverter))]
    public int LineOfBusiness { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusText => Status switch { 1 => "Active", 2 => "Pending", 3 => "Terminated", 4 => "Suspended", 5 => "COBRA", _ => "Unknown" };
}
