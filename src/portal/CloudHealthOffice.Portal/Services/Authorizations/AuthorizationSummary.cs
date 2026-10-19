using System.Text.Json.Serialization;

namespace CloudHealthOffice.Portal.Services;

public class AuthorizationSummary
{
    // Fields matching the Authorization API response
    public string Id { get; set; } = string.Empty;
    public string AuthorizationNumber { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string PatientFirstName { get; set; } = string.Empty;
    public string PatientLastName { get; set; } = string.Empty;
    public string RequestingProviderName { get; set; } = string.Empty;
    public string ServiceTypeCode { get; set; } = string.Empty;
    /// <summary>
    /// authorization-service writes the status by name ("Approved"); older
    /// payloads used the number. Either reads into the number StatusText maps.
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(AuthorizationStatusNumberConverter))]
    public int Status { get; set; }
    public DateTime SubmittedDate { get; set; }
    public DateTime? ReviewedDate { get; set; }

    // Computed display properties
    [System.Text.Json.Serialization.JsonIgnore]
    public string AuthorizationId => AuthorizationNumber;
    [System.Text.Json.Serialization.JsonIgnore]
    public string MemberName => $"{PatientFirstName} {PatientLastName}".Trim();
    [System.Text.Json.Serialization.JsonIgnore]
    public string ProviderName => RequestingProviderName;
    [System.Text.Json.Serialization.JsonIgnore]
    public string ServiceType => ServiceTypeCode;
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime RequestDate => SubmittedDate;
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime? DecisionDate => ReviewedDate;
    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusText => Status switch
    {
        1 => "Submitted", 2 => "InReview", 3 => "Pended",
        4 => "Approved", 5 => "Modified", 6 => "Denied",
        7 => "Expired", 8 => "Cancelled", _ => "Unknown"
    };
}
