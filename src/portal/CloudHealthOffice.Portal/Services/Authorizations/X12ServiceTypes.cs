namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// Names for the common X12 service type codes (278 UM03 / 270 EQ01) an
/// authorization carries, so a reader sees "Medical Care (1)" rather than "1".
/// A code not listed, or a value that is already a name, is shown as it is.
/// </summary>
public static class X12ServiceTypes
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1"] = "Medical Care", ["2"] = "Surgery", ["3"] = "Consultation", ["4"] = "Diagnostic X-Ray",
        ["5"] = "Diagnostic Lab", ["6"] = "Radiation Therapy", ["7"] = "Anesthesia", ["12"] = "DME Purchase",
        ["18"] = "DME Rental", ["30"] = "Health Benefit Plan Coverage", ["33"] = "Chiropractic",
        ["35"] = "Dental Care", ["42"] = "Home Health Care", ["45"] = "Hospice", ["47"] = "Hospital",
        ["48"] = "Hospital - Inpatient", ["50"] = "Hospital - Outpatient", ["56"] = "Medically Related Transportation",
        ["62"] = "MRI/CAT Scan", ["86"] = "Emergency Services", ["88"] = "Pharmacy",
        ["98"] = "Professional (Physician) Visit - Office", ["A4"] = "Psychiatric", ["AD"] = "Occupational Therapy",
        ["AE"] = "Physical Medicine", ["AF"] = "Speech Therapy", ["MH"] = "Mental Health", ["PT"] = "Physical Therapy",
        ["UC"] = "Urgent Care",
    };

    public static string Describe(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;
        var trimmed = code.Trim();
        return Names.TryGetValue(trimmed, out var name) ? $"{name} ({trimmed})" : trimmed;
    }
}
