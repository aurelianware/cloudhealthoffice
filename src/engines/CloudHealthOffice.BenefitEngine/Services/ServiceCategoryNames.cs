namespace CloudHealthOffice.BenefitEngine.Services;

/// <summary>
/// The named benefit categories the resolver's system-level fallbacks emit,
/// and the X12 5010 service type codes (element 1365, the 271 EB03 values)
/// each one corresponds to.
///
/// <para>
/// Plans are authored with named categories (the vocabulary of
/// <c>schemas/service-category-mappings/system-defaults.json</c>:
/// "Office Visit", "Inpatient Hospital", ...), and the engine joins the
/// resolver's <see cref="ServiceCategoryMatch.ServiceTypeCode"/> to the plan's
/// category by exact match. Both fallbacks — professional place-of-service
/// inference and institutional type-of-bill / revenue-code inference — work
/// out an X12 code and translate it here, so they always produce a name a
/// plan can match. Every name below is a category in that bundle except
/// <see cref="Dental"/>, which no fallback emits any more but stays so
/// plans keyed by <c>"35"</c> keep their alias.
/// </para>
///
/// <para>
/// Some plans are still keyed by X12 codes (<c>"98"</c>, <c>"48"</c>).
/// <see cref="BenefitPlanConfig.GetCategories"/> falls back to
/// <see cref="X12CodesFor"/> when no category matches the name exactly, so
/// those plans keep matching. A name can stand for more than one X12 code
/// (Behavioral Health is A4 psychiatric, MH mental health and AI substance
/// abuse); the first code listed for a name is its primary code
/// (<see cref="X12CodeFor"/>). X12 codes are unchanged everywhere else
/// (eligibility 270/271 service type codes come from the plan, not from here).
/// </para>
/// </summary>
public static class ServiceCategoryNames
{
    public const string OfficeVisit = "Office Visit";
    public const string InpatientHospital = "Inpatient Hospital";
    public const string OutpatientHospital = "Outpatient Hospital";
    public const string OutpatientSurgery = "Outpatient Surgery";
    public const string EmergencyRoom = "Emergency Room";
    public const string UrgentCare = "Urgent Care";
    public const string SkilledNursing = "Skilled Nursing";
    public const string HomeHealth = "Home Health";
    public const string Hospice = "Hospice";
    public const string BehavioralHealth = "Behavioral Health";
    public const string Laboratory = "Laboratory";
    public const string PhysicalTherapy = "Physical Therapy";
    public const string Dental = "Dental";

    /// <summary>X12 service type code → category name. Several codes may share a name; the first one listed is that name's primary code.</summary>
    private static readonly (string X12, string Name)[] Table =
    [
        ("98", OfficeVisit),        // Professional (Physician) Visit - Office
        ("48", InpatientHospital),  // Hospital - Inpatient
        ("50", OutpatientHospital), // Hospital - Outpatient
        ("13", OutpatientSurgery),  // Ambulatory Service Center Facility
        ("86", EmergencyRoom),      // Emergency Services
        ("UC", UrgentCare),         // Urgent Care
        ("AG", SkilledNursing),     // Skilled Nursing Care
        ("42", HomeHealth),         // Home Health Care
        ("45", Hospice),            // Hospice
        ("A4", BehavioralHealth),   // Psychiatric (primary)
        ("MH", BehavioralHealth),   // Mental Health
        ("AI", BehavioralHealth),   // Substance Abuse
        ("5", Laboratory),          // Diagnostic Lab
        ("PT", PhysicalTherapy),    // Physical Therapy
        ("35", Dental),             // Dental Care
    ];

    private static readonly IReadOnlyDictionary<string, string> NameByX12 =
        Table.ToDictionary(e => e.X12, e => e.Name, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> X12ByName =
        Table.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(e => e.X12).ToArray(),
                StringComparer.OrdinalIgnoreCase);

    /// <summary>Every X12 code that has a category name here.</summary>
    public static IReadOnlyCollection<string> KnownX12Codes => NameByX12.Keys.ToArray();

    /// <summary>The category name for an X12 service type code, or null when it is not one the fallbacks emit.</summary>
    public static string? NameFor(string x12Code) =>
        NameByX12.TryGetValue(x12Code, out var name) ? name : null;

    /// <summary>The primary X12 service type code for a category name, or null when the name has none here.</summary>
    public static string? X12CodeFor(string? name) => X12CodesFor(name).FirstOrDefault();

    /// <summary>Every X12 service type code for a category name, primary first; empty when the name has none here.</summary>
    public static IReadOnlyList<string> X12CodesFor(string? name) =>
        name is not null && X12ByName.TryGetValue(name.Trim(), out var codes) ? codes : [];
}
