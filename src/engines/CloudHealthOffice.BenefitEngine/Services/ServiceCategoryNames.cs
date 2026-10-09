namespace CloudHealthOffice.BenefitEngine.Services;

/// <summary>
/// The named benefit categories the resolver's system-level fallbacks emit,
/// and the X12 5010 service type code each one corresponds to.
///
/// <para>
/// Plans are authored with named categories (the vocabulary of
/// <c>schemas/service-category-mappings/system-defaults.json</c>:
/// "Office Visit", "Inpatient Hospital", ...), and the engine joins the
/// resolver's <see cref="ServiceCategoryMatch.ServiceTypeCode"/> to the plan's
/// category by exact match. Both fallbacks — professional place-of-service
/// inference and institutional type-of-bill / revenue-code inference — work
/// out an X12 code and translate it here, so they always produce a name a
/// plan can match.
/// </para>
///
/// <para>
/// Some plans are still keyed by X12 codes (<c>"98"</c>, <c>"48"</c>).
/// <see cref="BenefitPlanConfig.GetCategories"/> falls back to
/// <see cref="X12CodeFor"/> when no category matches the name exactly, so
/// those plans keep matching. X12 codes are unchanged everywhere else
/// (eligibility 270/271 service type codes come from the plan, not from here).
/// </para>
/// </summary>
public static class ServiceCategoryNames
{
    public const string OfficeVisit = "Office Visit";
    public const string InpatientHospital = "Inpatient Hospital";
    public const string OutpatientHospital = "Outpatient Hospital";
    public const string EmergencyRoom = "Emergency Room";
    public const string SkilledNursing = "Skilled Nursing";
    public const string HomeHealth = "Home Health";
    public const string Hospice = "Hospice";
    public const string BehavioralHealth = "Behavioral Health";
    public const string Dental = "Dental";

    private static readonly IReadOnlyDictionary<string, string> NameByX12 =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["98"] = OfficeVisit,        // Professional (Physician) Visit - Office
            ["48"] = InpatientHospital,  // Hospital - Inpatient
            ["50"] = OutpatientHospital, // Hospital - Outpatient
            ["86"] = EmergencyRoom,      // Emergency Services
            ["AG"] = SkilledNursing,     // Skilled Nursing Care
            ["42"] = HomeHealth,         // Home Health Care
            ["45"] = Hospice,            // Hospice
            ["A4"] = BehavioralHealth,   // Psychiatric
            ["35"] = Dental,             // Dental Care
        };

    private static readonly IReadOnlyDictionary<string, string> X12ByName =
        NameByX12.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>The category name for an X12 service type code, or null when it is not one the fallbacks emit.</summary>
    public static string? NameFor(string x12Code) =>
        NameByX12.TryGetValue(x12Code, out var name) ? name : null;

    /// <summary>The X12 service type code for a category name, or null when the name has none here.</summary>
    public static string? X12CodeFor(string? name) =>
        name is not null && X12ByName.TryGetValue(name.Trim(), out var code) ? code : null;
}
