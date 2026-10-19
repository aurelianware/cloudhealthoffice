using System.Text.Json.Serialization;

namespace CloudHealthOffice.Portal.Services;

public class DeductibleInfo
{
    [JsonPropertyName("individualDeductible")]
    public decimal IndividualAmount { get; set; }  // EB*C*30*HLT*IND

    [JsonPropertyName("individualDeductibleMet")]
    public decimal IndividualMet { get; set; }  // EB*C accumulated

    [JsonPropertyName("individualDeductibleRemaining")]
    public decimal IndividualRemaining { get; set; }  // EB*D remaining

    [JsonPropertyName("familyDeductible")]
    public decimal FamilyAmount { get; set; }  // EB*C*30*HLT*FAM

    [JsonPropertyName("familyDeductibleMet")]
    public decimal FamilyMet { get; set; }

    [JsonPropertyName("familyDeductibleRemaining")]
    public decimal FamilyRemaining { get; set; }

    public string TimePeriod { get; set; } = "Calendar Year";  // EB06: 29=Year
}
