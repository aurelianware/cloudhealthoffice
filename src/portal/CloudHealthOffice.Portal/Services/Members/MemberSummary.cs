using System.Text.Json.Serialization;

namespace CloudHealthOffice.Portal.Services;

public class MemberSummary
{
    public string MemberId { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public DateTime DateOfBirth { get; set; }
    public string CoverageStatus { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayStatus => string.IsNullOrWhiteSpace(CoverageStatus) ? Status : CoverageStatus;
}
