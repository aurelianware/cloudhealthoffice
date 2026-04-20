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
    /// Return the plan's original <c>ServiceCategory</c> string, trimmed
    /// only, when it looks like a pharmacy tier label; <c>null</c> otherwise.
    ///
    /// This is the value the UI displays. It is deliberately <em>not</em>
    /// normalized, re-cased, or collapsed — a plan that configures
    /// "Specialty Drug" must render as "Specialty Drug", not "Specialty".
    /// For a normalized bucket suitable for grouping or analytics, use
    /// <see cref="ExtractCanonicalTier"/>.
    /// </summary>
    public static string? ExtractTierLabel(string? serviceCategory)
    {
        if (string.IsNullOrWhiteSpace(serviceCategory))
            return null;

        var s = serviceCategory.Trim();
        if (s.StartsWith("Tier ", StringComparison.OrdinalIgnoreCase)) return s;
        if (s.Equals("Generic", StringComparison.OrdinalIgnoreCase)) return s;
        if (s.Equals("Preferred Brand", StringComparison.OrdinalIgnoreCase)) return s;
        if (s.Equals("Non-Preferred Brand", StringComparison.OrdinalIgnoreCase)) return s;
        if (s.Contains("specialty", StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    /// <summary>
    /// Normalize a pharmacy-tier <c>ServiceCategory</c> to a stable bucket
    /// key for grouping and downstream analytics: <c>Tier1</c>, <c>Tier2</c>,
    /// <c>Tier3</c>, <c>Tier4</c>, <c>Generic</c>, <c>PreferredBrand</c>,
    /// <c>NonPreferredBrand</c>, or <c>Specialty</c>. Returns <c>null</c>
    /// when the input does not match a known tier shape.
    ///
    /// Callers MUST NOT display this value — it is lossy by design (e.g.
    /// "Specialty Drug" → "Specialty"). Display <see cref="ExtractTierLabel"/>
    /// instead.
    /// </summary>
    public static string? ExtractCanonicalTier(string? serviceCategory)
    {
        if (string.IsNullOrWhiteSpace(serviceCategory))
            return null;

        var s = serviceCategory.Trim();
        if (s.StartsWith("Tier 1", StringComparison.OrdinalIgnoreCase)) return "Tier1";
        if (s.StartsWith("Tier 2", StringComparison.OrdinalIgnoreCase)) return "Tier2";
        if (s.StartsWith("Tier 3", StringComparison.OrdinalIgnoreCase)) return "Tier3";
        if (s.StartsWith("Tier 4", StringComparison.OrdinalIgnoreCase)) return "Tier4";
        if (s.Equals("Generic", StringComparison.OrdinalIgnoreCase)) return "Generic";
        if (s.Equals("Preferred Brand", StringComparison.OrdinalIgnoreCase)) return "PreferredBrand";
        if (s.Equals("Non-Preferred Brand", StringComparison.OrdinalIgnoreCase)) return "NonPreferredBrand";
        if (s.Contains("specialty", StringComparison.OrdinalIgnoreCase)) return "Specialty";
        return null;
    }

    /// <summary>
    /// Return true if the raw <c>ServiceCategory</c> denotes a specialty
    /// pharmacy benefit — a case-insensitive contains-check for "specialty".
    /// </summary>
    public static bool IsSpecialty(string? serviceCategory) =>
        !string.IsNullOrWhiteSpace(serviceCategory)
        && serviceCategory.Contains("specialty", StringComparison.OrdinalIgnoreCase);
}
