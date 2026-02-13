namespace ReferenceDataService.Models;

/// <summary>
/// Code validation response (for claims adjudication)
/// </summary>
public class CodeValidationResponse
{
    public string Code { get; set; } = string.Empty;
    public bool IsValid { get; set; }
    public string CodeType { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Status { get; set; }
    public bool? IsBillable { get; set; }
    public bool RequiresPriorAuth { get; set; }
    public string? ValidationMessage { get; set; }
}

/// <summary>
/// Reference data statistics
/// </summary>
public class ReferenceDataStats
{
    public int TotalCptCodes { get; set; }
    public int ActiveCptCodes { get; set; }
    public int TotalIcd10Codes { get; set; }
    public int BillableIcd10Codes { get; set; }
    public int TotalHcpcsCodes { get; set; }
    public int TotalModifiers { get; set; }
    public int TotalDrgCodes { get; set; }
    public int TotalPlacesOfService { get; set; }
    public int TotalRevenueCodes { get; set; }
    public DateTime? LastUpdated { get; set; }
}
