namespace EligibilityService.Services;

/// <summary>
/// X12 service type codes (EB03 / EQ01) for the 271, and the mapping from a
/// benefit plan's free-text service categories ("Office Visit", "Urgent
/// Care") to them. A 271 may only carry codes from the X12 list, so a benefit
/// whose category maps to no code is left out of the 271 rather than sent
/// with a label in EB03.
/// </summary>
public static class X12ServiceTypes
{
    public const string HealthBenefitPlanCoverage = "30";

    /// <summary>
    /// The service types CAQH CORE requires a 271 to answer for a generic
    /// (EQ*30) inquiry, when the plan covers them: Medical Care,
    /// Chiropractic, Dental Care, Hospital, Emergency Services, Pharmacy,
    /// Professional (Physician) Visit - Office, Vision (Optometry), Mental
    /// Health, Urgent Care.
    /// </summary>
    public static readonly IReadOnlyList<string> CoreGeneric = ["1", "33", "35", "47", "86", "88", "98", "AL", "MH", "UC"];

    /// <summary>Service type codes CHO recognises as codes (not labels) when a plan uses them as its category.</summary>
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "1", "2", "3", "4", "5", "6", "7", "12", "13", "14", "18", "20", "23", "24", "25", "26", "27", "28",
        "30", "33", "35", "36", "37", "38", "39", "40", "41", "42", "45", "47", "48", "50", "51", "52", "53",
        "54", "56", "57", "58", "59", "60", "61", "62", "63", "64", "65", "66", "67", "68", "69", "70", "71",
        "72", "73", "76", "78", "79", "80", "81", "82", "83", "84", "85", "86", "88", "89", "90", "91", "92",
        "93", "94", "95", "96", "97", "98", "99", "A0", "A3", "A4", "A6", "A7", "A8", "AD", "AE", "AF", "AG",
        "AH", "AI", "AJ", "AK", "AL", "AM", "BG", "BH", "BT", "BU", "CE", "DM", "EA", "MH", "PT", "UC",
    };

    /// <summary>Common plan category labels, normalised (lower case, letters and digits only).</summary>
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["medical"] = "1", ["medicalcare"] = "1",
        ["officevisit"] = "98", ["primarycare"] = "98", ["pcp"] = "98", ["physicianvisit"] = "98",
        ["specialist"] = "98", ["specialistvisit"] = "98", ["telehealth"] = "98",
        ["urgentcare"] = "UC",
        ["emergency"] = "86", ["emergencyroom"] = "86", ["emergencyservices"] = "86", ["er"] = "86",
        ["hospital"] = "47", ["inpatient"] = "48", ["inpatienthospital"] = "48", ["hospitalinpatient"] = "48",
        ["outpatient"] = "50", ["outpatienthospital"] = "50", ["hospitaloutpatient"] = "50",
        ["mentalhealth"] = "MH", ["behavioralhealth"] = "MH",
        ["substanceabuse"] = "AI", ["substanceusedisorder"] = "AI",
        ["chiropractic"] = "33",
        ["dental"] = "35", ["dentalcare"] = "35", ["preventivedental"] = "41", ["orthodontics"] = "38",
        ["vision"] = "AL", ["optometry"] = "AL", ["eyeexam"] = "AL",
        ["pharmacy"] = "88", ["prescriptiondrugs"] = "88", ["prescriptions"] = "88", ["rx"] = "88",
        ["lab"] = "5", ["laboratory"] = "5", ["diagnosticlab"] = "5",
        ["xray"] = "4", ["diagnosticxray"] = "4", ["imaging"] = "4", ["radiology"] = "4",
        ["surgery"] = "2", ["maternity"] = "69", ["physicaltherapy"] = "PT",
        ["durablemedicalequipment"] = "DM", ["dme"] = "DM",
        ["homehealth"] = "42", ["hospice"] = "45", ["skillednursing"] = "AG",
    };

    /// <summary>The X12 code for a plan category: the category itself if it is a code, else a known label; null if neither.</summary>
    public static string? FromCategory(string? category, IReadOnlyDictionary<string, string>? overrides = null)
    {
        if (string.IsNullOrWhiteSpace(category)) return null;
        var trimmed = category.Trim();
        if (overrides is not null && overrides.TryGetValue(trimmed, out var configured)) return configured;
        if (Known.Contains(trimmed)) return trimmed.ToUpperInvariant();
        var key = new string(trimmed.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return Labels.TryGetValue(key, out var code) ? code : null;
    }

    public static bool IsCode(string? value) => value is not null && Known.Contains(value);
}
