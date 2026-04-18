namespace BenefitPlanService.Services;

/// <summary>
/// Static lookup that translates raw <c>Benefit.ServiceCategory</c> strings
/// into the canonical member-view category keys the portal renders.
///
/// Keys are case-insensitive. Values are stable identifiers, not display
/// labels — the portal owns presentation. Unmapped inputs fall through to
/// <see cref="Other"/>; callers MUST log the unmapped value at Information
/// level so gaps surface in telemetry (see BenefitViewService).
///
/// Kept intentionally outside the service class so the mapping can be unit
/// tested and extended without touching service logic. If this list grows
/// past ~100 entries, lift it into config/benefit-category-map.json.
/// </summary>
public static class BenefitCategoryMap
{
    public const string PrimaryCare      = "PrimaryCare";
    public const string Specialist       = "Specialist";
    public const string EmergencyRoom    = "EmergencyRoom";
    public const string UrgentCare       = "UrgentCare";
    public const string Hospital         = "Hospital";
    public const string Pharmacy         = "Pharmacy";
    public const string DurableMedical   = "DurableMedicalEquipment";
    public const string MentalHealth     = "MentalHealth";
    public const string Maternity        = "Maternity";
    public const string Preventive       = "Preventive";
    public const string Other            = "Other";

    private static readonly Dictionary<string, string> _map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Primary care
            ["primary care"]                = PrimaryCare,
            ["primary_care"]                = PrimaryCare,
            ["pcp"]                         = PrimaryCare,
            ["office visit"]                = PrimaryCare,

            // Specialist
            ["specialist"]                  = Specialist,
            ["specialty care"]              = Specialist,
            ["specialist office visit"]     = Specialist,

            // Emergency
            ["emergency"]                   = EmergencyRoom,
            ["emergency room"]              = EmergencyRoom,
            ["er"]                          = EmergencyRoom,
            ["ed"]                          = EmergencyRoom,

            // Urgent care
            ["urgent care"]                 = UrgentCare,
            ["urgent_care"]                 = UrgentCare,
            ["uc"]                          = UrgentCare,

            // Hospital / inpatient / outpatient
            ["hospital"]                    = Hospital,
            ["inpatient"]                   = Hospital,
            ["inpatient hospital"]          = Hospital,
            ["outpatient"]                  = Hospital,
            ["outpatient hospital"]         = Hospital,
            ["surgery"]                     = Hospital,

            // Pharmacy (tier detail carried separately in PharmacyDetail)
            ["pharmacy"]                    = Pharmacy,
            ["rx"]                          = Pharmacy,
            ["retail pharmacy"]             = Pharmacy,
            ["mail order pharmacy"]         = Pharmacy,
            ["tier 1"]                      = Pharmacy,
            ["tier 2"]                      = Pharmacy,
            ["tier 3"]                      = Pharmacy,
            ["tier 4"]                      = Pharmacy,
            ["generic"]                     = Pharmacy,
            ["preferred brand"]             = Pharmacy,
            ["non-preferred brand"]         = Pharmacy,
            ["specialty drug"]              = Pharmacy,
            ["specialty pharmacy"]          = Pharmacy,

            // DME
            ["dme"]                         = DurableMedical,
            ["durable medical equipment"]   = DurableMedical,

            // Behavioral / mental health
            ["mental health"]               = MentalHealth,
            ["behavioral health"]           = MentalHealth,
            ["substance abuse"]             = MentalHealth,
            ["substance use"]               = MentalHealth,

            // Maternity
            ["maternity"]                   = Maternity,
            ["prenatal"]                    = Maternity,
            ["delivery"]                    = Maternity,
            ["postpartum"]                  = Maternity,

            // Preventive
            ["preventive"]                  = Preventive,
            ["preventative"]                = Preventive,
            ["wellness"]                    = Preventive,
            ["screening"]                   = Preventive,
            ["immunization"]                = Preventive,
        };

    /// <summary>
    /// Resolve a raw service category string to a canonical category key.
    /// Returns <c>(key, mapped)</c>; <c>mapped==false</c> means the value
    /// fell through to <see cref="Other"/> and should be logged.
    /// </summary>
    public static (string Category, bool Mapped) Resolve(string? serviceCategory)
    {
        if (string.IsNullOrWhiteSpace(serviceCategory))
            return (Other, false);

        var normalized = serviceCategory.Trim();
        if (_map.TryGetValue(normalized, out var mapped))
            return (mapped, true);

        return (Other, false);
    }

    /// <summary>
    /// Heuristic for classifying pharmacy tier labels when the benefit is
    /// in the Pharmacy category. Returns the raw tier label verbatim;
    /// callers render it directly. Extensible — no hard enum.
    /// </summary>
    public static string? ExtractPharmacyTier(string? serviceCategory)
    {
        if (string.IsNullOrWhiteSpace(serviceCategory))
            return null;

        var s = serviceCategory.Trim();
        // Keep original casing when it's already a clean tier label.
        if (s.StartsWith("Tier ", StringComparison.OrdinalIgnoreCase)) return s;
        if (s.Equals("Generic", StringComparison.OrdinalIgnoreCase)) return "Generic";
        if (s.Equals("Preferred Brand", StringComparison.OrdinalIgnoreCase)) return "Preferred Brand";
        if (s.Equals("Non-Preferred Brand", StringComparison.OrdinalIgnoreCase)) return "Non-Preferred Brand";
        if (s.Contains("specialty", StringComparison.OrdinalIgnoreCase)) return "Specialty";
        return null;
    }
}
