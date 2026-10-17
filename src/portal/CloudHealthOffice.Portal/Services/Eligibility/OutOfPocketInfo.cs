using System.Text.Json.Serialization;

namespace CloudHealthOffice.Portal.Services;

public class OutOfPocketInfo
{
    [JsonPropertyName("individualOOPMax")]
    public decimal IndividualAmount { get; set; }  // EB*G*30*HLT*IND

    [JsonPropertyName("individualOOPMet")]
    public decimal IndividualMet { get; set; }

    [JsonPropertyName("individualOOPRemaining")]
    public decimal IndividualRemaining { get; set; }

    [JsonPropertyName("familyOOPMax")]
    public decimal FamilyAmount { get; set; }  // EB*G*30*HLT*FAM

    [JsonPropertyName("familyOOPMet")]
    public decimal FamilyMet { get; set; }

    [JsonPropertyName("familyOOPRemaining")]
    public decimal FamilyRemaining { get; set; }

    public string TimePeriod { get; set; } = "Calendar Year";
}
