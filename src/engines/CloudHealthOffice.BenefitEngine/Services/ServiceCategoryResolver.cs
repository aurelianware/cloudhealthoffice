using CloudHealthOffice.BenefitEngine.Domain;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.BenefitEngine.Services;

/// <summary>
/// Resolves a procedure code + context to a benefit service type code.
///
/// This is the critical "glue" between the claim world (CPT/HCPCS codes)
/// and the benefit world (service type codes). Without this, you can't
/// look up what copay/coinsurance applies to a given procedure.
///
/// QNXT equivalent: The procedure-to-service-category cross-reference tables.
///
/// Resolution order:
/// 1. Plan-specific overrides (if configured)
/// 2. Tenant-level default mappings
/// 3. System-level fallback mappings
/// </summary>
public interface IServiceCategoryResolver
{
    /// <summary>
    /// Resolve a procedure code to a service type code.
    /// Returns null if no mapping is found (which would be an adjudication error).
    ///
    /// <para>
    /// <paramref name="serviceDate"/> is the claim line's service date,
    /// used to filter mappings by their inclusive
    /// <see cref="Domain.ServiceCategoryMapping.EffectiveStart"/> /
    /// <see cref="Domain.ServiceCategoryMapping.EffectiveEnd"/> window
    /// and the <see cref="Domain.ServiceCategoryMapping.IsActive"/>
    /// kill-switch (capability BP 5.10). A claim adjudicated in 2027
    /// for service performed on 2026-08-15 hits 2026 mappings.
    /// </para>
    /// </summary>
    Task<ServiceCategoryMatch?> ResolveAsync(
        string tenantId,
        Guid benefitPlanId,
        DateOnly serviceDate,
        string procedureCode,
        string codeType,
        string placeOfService,
        IReadOnlyList<string> modifiers,
        string? revenueCode,
        ServiceCategoryClaimContext? claim = null,
        CancellationToken ct = default);
}

/// <summary>
/// Claim-level context for the system-level fallback in
/// <see cref="IServiceCategoryResolver.ResolveAsync"/>.
/// <para>
/// What the place-of-service argument holds depends on the caller. The
/// claims-service 837I mapping puts CLM05-1 there, the facility type code
/// (the first two digits of the type of bill): "11" is a hospital inpatient
/// bill, not an office, and "21" is a skilled nursing facility, not an
/// inpatient hospital. The synchronous adjudication and payment-estimate APIs
/// put a real CMS place of service there, even for institutional claims. So
/// the resolver reads it as a facility type only when
/// <paramref name="PlaceOfServiceIsFacilityType"/> says so; being
/// institutional is not enough.
/// </para>
/// </summary>
/// <param name="ClaimType">"837P", "837I" or "837D" (the engine's claim type), or null.</param>
/// <param name="TypeOfBill">
/// NUBC type of bill: exactly three digits (facility type + frequency, e.g.
/// "111") or four with a leading zero ("0111"). Anything else is ignored, as
/// if no type of bill had been sent.
/// </param>
/// <param name="PlaceOfServiceIsFacilityType">
/// True only when the caller knows the place-of-service argument carries
/// CLM05-1 (the facility type code) rather than a CMS place of service.
/// </param>
public sealed record ServiceCategoryClaimContext(
    string? ClaimType,
    string? TypeOfBill,
    bool PlaceOfServiceIsFacilityType = false)
{
    /// <summary>The type of bill as three digits, or null when absent or malformed.</summary>
    public string? NormalizedTypeOfBill => NormalizeTypeOfBill(TypeOfBill);

    /// <summary>An institutional claim: claim type 837I, or a valid type of bill is present.</summary>
    public bool IsInstitutional =>
        string.Equals(ClaimType?.Trim(), "837I", StringComparison.OrdinalIgnoreCase)
        || string.Equals(ClaimType?.Trim(), "Institutional", StringComparison.OrdinalIgnoreCase)
        || NormalizedTypeOfBill is not null;

    /// <summary>
    /// "111" → "111", "0111" → "111"; null for anything else ("11",
    /// "111garbage", or a four-digit value without the leading zero such as "1111").
    /// Delegates to <see cref="CloudHealthOffice.ReferenceData.Domain.NubcTypeOfBill.Normalize"/>,
    /// the same rule the fee schedule engine uses to decide the facility setting.
    /// </summary>
    public static string? NormalizeTypeOfBill(string? typeOfBill)
        => CloudHealthOffice.ReferenceData.Domain.NubcTypeOfBill.Normalize(typeOfBill);
}

public record ServiceCategoryMatch
{
    public string ServiceTypeCode { get; init; } = default!;
    public string ServiceTypeDescription { get; init; } = default!;
    public string MatchedBy { get; init; } = default!; // "PlanOverride", "TenantDefault", "SystemDefault"
    public string MatchedRule { get; init; } = default!; // For audit: which rule matched
}

/// <summary>
/// Default implementation using the ServiceCategoryMapping entities.
/// </summary>
public class ServiceCategoryResolver : IServiceCategoryResolver
{
    private readonly IServiceCategoryMappingRepository _repo;
    private readonly ILogger<ServiceCategoryResolver> _logger;

    public ServiceCategoryResolver(
        IServiceCategoryMappingRepository repo,
        ILogger<ServiceCategoryResolver> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task<ServiceCategoryMatch?> ResolveAsync(
        string tenantId,
        Guid benefitPlanId,
        DateOnly serviceDate,
        string procedureCode,
        string codeType,
        string placeOfService,
        IReadOnlyList<string> modifiers,
        string? revenueCode,
        ServiceCategoryClaimContext? claim = null,
        CancellationToken ct = default)
    {
        // 1. Try plan-specific overrides first
        var planMappings = await _repo.GetMappingsAsync(tenantId, benefitPlanId, ct);
        var match = FindMatch(planMappings, serviceDate, procedureCode, codeType, placeOfService, modifiers, revenueCode,
            tenantId, "plan");
        if (match is not null)
        {
            return match with { MatchedBy = "PlanOverride" };
        }

        // 2. Try tenant-level defaults (BenefitPlanId = null)
        var tenantMappings = await _repo.GetMappingsAsync(tenantId, null, ct);
        match = FindMatch(tenantMappings, serviceDate, procedureCode, codeType, placeOfService, modifiers, revenueCode,
            tenantId, "tenant");
        if (match is not null)
        {
            return match with { MatchedBy = "TenantDefault" };
        }

        // 3. System-level fallback. Institutional claims: infer from the
        //    type of bill / facility type and the revenue code first.
        if (claim is { IsInstitutional: true })
        {
            var institutional = InferInstitutional(claim, placeOfService, revenueCode);
            if (institutional is not null)
            {
                _logger.LogWarning(
                    "No explicit mapping for institutional {CodeType} {ProcedureCode} REV {RevenueCode} — " +
                    "falling back to type-of-bill / revenue-code inference ({Rule}): {ServiceTypeCode}",
                    SanitizeForLog(codeType), SanitizeForLog(procedureCode), SanitizeForLog(revenueCode),
                    SanitizeForLog(institutional.MatchedRule), institutional.ServiceTypeCode);
                return institutional;
            }
        }

        // When the place-of-service slot holds CLM05-1 (claims-service 837I),
        // it is a facility type, not a place of service: facility type "11"
        // is a hospital inpatient bill, and reading it as POS 11 (office)
        // denied inpatient stays as office visits. Never infer from it.
        if (claim is { PlaceOfServiceIsFacilityType: true })
        {
            _logger.LogError(
                "No service category mapping found for institutional {CodeType} {ProcedureCode} REV {RevenueCode} TOB {TypeOfBill}",
                SanitizeForLog(codeType), SanitizeForLog(procedureCode), SanitizeForLog(revenueCode),
                SanitizeForLog(claim.NormalizedTypeOfBill ?? "(none)"));
            return null;
        }

        // Otherwise the slot is a real CMS place of service (professional
        // claims, and institutional claims from the synchronous APIs).

        var fallback = InferFromPlaceOfService(placeOfService, procedureCode);
        if (fallback is not null)
        {
            _logger.LogWarning(
                "No explicit mapping for {CodeType} {ProcedureCode} POS {POS} — " +
                "falling back to POS-based inference: {ServiceTypeCode}",
                SanitizeForLog(codeType), SanitizeForLog(procedureCode), SanitizeForLog(placeOfService),
                fallback.ServiceTypeCode);
            return fallback;
        }

        _logger.LogError(
            "No service category mapping found for {CodeType} {ProcedureCode} POS {POS}",
            SanitizeForLog(codeType), SanitizeForLog(procedureCode), SanitizeForLog(placeOfService));
        return null;
    }

    /// <summary>
    /// Claim codes come from submitted claims; strip line breaks and other
    /// control characters and cap the length before logging them (log forging).
    /// </summary>
    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var cleaned = value.Replace("\r", string.Empty).Replace("\n", string.Empty);
        cleaned = new string(cleaned.Where(c => !char.IsControl(c)).ToArray());
        return cleaned.Length <= MaxLoggedCodeLength ? cleaned : cleaned[..MaxLoggedCodeLength];
    }

    private const int MaxLoggedCodeLength = 32;

    private ServiceCategoryMatch? FindMatch(
        IReadOnlyList<ServiceCategoryMapping> mappings,
        DateOnly serviceDate,
        string procedureCode,
        string codeType,
        string placeOfService,
        IReadOnlyList<string> modifiers,
        string? revenueCode,
        string tenantId,
        string scope)
    {
        // Filter by IsActive + the inclusive [EffectiveStart, EffectiveEnd]
        // window against the claim line's service date (capability BP 5.10).
        // null bound = open; IsActive=false drops the row regardless of
        // window. Filtering is in-memory over the cached list — the repo
        // seam (GetMappingsAsync) is unchanged.
        var filtered = new List<ServiceCategoryMapping>(mappings.Count);
        foreach (var mapping in mappings)
        {
            if (IsInEffect(mapping, serviceDate))
            {
                filtered.Add(mapping);
            }
        }

        if (mappings.Count > 0 && filtered.Count < mappings.Count)
        {
            BenefitEngineMetrics.ScmFilteredByEffectiveWindow.Add(1,
                new KeyValuePair<string, object?>("cho.tenant_id", tenantId),
                new KeyValuePair<string, object?>("cho.scope", scope));
        }

        foreach (var mapping in filtered)
        {
            // Evaluate rules in priority order
            var sortedRules = mapping.Rules.OrderBy(r => r.Priority);

            foreach (var rule in sortedRules)
            {
                if (RuleMatches(rule, procedureCode, codeType, placeOfService, modifiers, revenueCode))
                {
                    return new ServiceCategoryMatch
                    {
                        ServiceTypeCode = mapping.ServiceTypeCode,
                        ServiceTypeDescription = mapping.ServiceTypeDescription,
                        MatchedRule = $"{rule.CodeType}:{rule.CodePattern}" +
                            (rule.PlaceOfServiceCode is not null ? $"/POS:{rule.PlaceOfServiceCode}" : "") +
                            (rule.RequiredModifier is not null ? $"/MOD:{rule.RequiredModifier}" : "")
                    };
                }
            }
        }

        return null;
    }

    private static bool IsInEffect(ServiceCategoryMapping mapping, DateOnly serviceDate)
    {
        if (!mapping.IsActive) return false;
        if (mapping.EffectiveStart is { } start && serviceDate < start) return false;
        if (mapping.EffectiveEnd is { } end && serviceDate > end) return false;
        return true;
    }

    /// <summary><see cref="ProcedureCodeRule.CodeType"/> of a rule matched on the line's revenue code.</summary>
    public const string RevenueCodeType = "REV";

    /// <summary>Revenue codes are four digits; "120" and "0120" are the same code.</summary>
    private static string? NormalizeRevenueCode(string? revenueCode)
        => string.IsNullOrWhiteSpace(revenueCode) ? null : revenueCode.Trim().PadLeft(4, '0');

    private static bool RuleMatches(
        ProcedureCodeRule rule,
        string procedureCode,
        string codeType,
        string placeOfService,
        IReadOnlyList<string> modifiers,
        string? revenueCode)
    {
        // A REV rule (UB-04 revenue code, e.g. 0100–0219 accommodation) is
        // matched against the line's revenue code, whatever the line's code
        // type: claim lines always arrive as "CPT", and an 837I line may
        // carry a revenue code with no HCPCS at all. Comparing a REV rule
        // to the code type and the (empty) procedure code meant no REV rule
        // ever matched, so an inpatient stay fell through to POS inference
        // on CLM05-1 (the facility type code, "11" for a hospital
        // inpatient bill) and resolved as an office visit.
        var isRevenueRule = string.Equals(rule.CodeType, RevenueCodeType, StringComparison.OrdinalIgnoreCase);
        if (isRevenueRule)
        {
            var revenue = NormalizeRevenueCode(revenueCode);
            if (revenue is null)
                return false;
            procedureCode = revenue;
        }
        else if (!string.Equals(rule.CodeType, codeType, StringComparison.OrdinalIgnoreCase))
        {
            // Code type must match
            return false;
        }

        // POS filter (if specified on rule)
        if (rule.PlaceOfServiceCode is not null &&
            !string.Equals(rule.PlaceOfServiceCode, placeOfService, StringComparison.OrdinalIgnoreCase))
            return false;

        // Modifier filter (if specified on rule)
        if (rule.RequiredModifier is not null &&
            !modifiers.Any(m => string.Equals(m, rule.RequiredModifier, StringComparison.OrdinalIgnoreCase)))
            return false;

        // Revenue code filter (if specified on rule)
        if (rule.RevenueCode is not null &&
            !string.Equals(rule.RevenueCode, revenueCode, StringComparison.OrdinalIgnoreCase))
            return false;

        // Code matching
        if (rule.CodeRangeEnd is not null)
        {
            // Range match: CodePattern is start, CodeRangeEnd is end
            return string.Compare(procedureCode, rule.CodePattern, StringComparison.OrdinalIgnoreCase) >= 0 &&
                   string.Compare(procedureCode, rule.CodeRangeEnd, StringComparison.OrdinalIgnoreCase) <= 0;
        }

        if (rule.CodePattern.EndsWith('*'))
        {
            // Prefix/wildcard match
            var prefix = rule.CodePattern[..^1];
            return procedureCode.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        // Exact match
        return string.Equals(procedureCode, rule.CodePattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Last-resort fallback for an institutional (UB-04 / 837I) claim, using
    /// NUBC conventions. The facility type is the first two digits of the
    /// type of bill (leading zero dropped: "0111" and "111" are both facility
    /// type "11"). Without a valid type of bill it is the place-of-service
    /// value, but only when the caller marked that value as CLM05-1
    /// (<see cref="ServiceCategoryClaimContext.PlaceOfServiceIsFacilityType"/>);
    /// a CMS place of service is never read as a facility type ("21" is an
    /// inpatient hospital as a POS but a skilled nursing facility as a facility type).
    /// <list type="number">
    ///   <item>An inpatient bill (11x/12x/41x hospital, 18x/28x swing bed,
    ///     21x/22x SNF, 81x/82x hospice) is one stay under one benefit: every
    ///     line takes the facility's category, whatever its revenue code.</item>
    ///   <item>Otherwise the line's revenue code decides when it is specific:
    ///     045x emergency room, 010x–021x accommodation (inpatient).</item>
    ///   <item>Otherwise the outpatient / other facility type: 13x/14x/43x
    ///     hospital outpatient, 85x critical access hospital outpatient,
    ///     32x–34x home health.</item>
    /// </list>
    /// Returns null when nothing is recognized (CARC 204 upstream) rather than
    /// guessing a professional category. The X12 code each step yields is
    /// translated to the plan-facing name by <see cref="ServiceCategoryNames"/>.
    /// </summary>
    private static ServiceCategoryMatch? InferInstitutional(
        ServiceCategoryClaimContext claim, string placeOfService, string? revenueCode)
    {
        var facilityType = claim.NormalizedTypeOfBill?[..2];
        var facilitySource = "TOB";
        if (facilityType is null
            && claim.PlaceOfServiceIsFacilityType
            && placeOfService is { Length: 2 } && placeOfService.All(char.IsAsciiDigit))
        {
            facilityType = placeOfService;
            facilitySource = "FacilityType";
        }

        // Inpatient-type bills first, ahead of the revenue code. Standard
        // convention: ER services that lead to the admission (revenue code
        // 045x on the inpatient bill) are bundled into the stay and paid
        // under the inpatient benefit, not split out as an ER visit.
        var inpatientCode = facilityType switch
        {
            "11" or "12" or "41" => "48",              // hospital inpatient
            "18" or "21" or "22" or "28" => "AG",      // SNF / swing bed
            "81" or "82" => "45",                      // hospice
            _ => null,
        };
        if (inpatientCode is not null)
            return SystemDefault(inpatientCode, $"{facilitySource}-fallback:{facilityType}");

        var revenue = NormalizeRevenueCode(revenueCode);
        if (revenue is not null && int.TryParse(revenue, out var rev))
        {
            if (rev is >= 450 and <= 459)
                return SystemDefault("86", $"REV-fallback:{revenue}");   // emergency room
            if (rev is >= 100 and <= 219)
                return SystemDefault("48", $"REV-fallback:{revenue}");   // accommodation
        }

        var code = facilityType switch
        {
            "13" or "14" or "43" or "85" => "50",      // hospital outpatient
            "32" or "33" or "34" => "42",              // home health
            _ => null,
        };
        return code is null ? null : SystemDefault(code, $"{facilitySource}-fallback:{facilityType}");
    }

    /// <summary>
    /// A system-level fallback match: the X12 service type code the inference
    /// produced, translated to the named category plans are authored with.
    /// </summary>
    private static ServiceCategoryMatch SystemDefault(string x12Code, string rule)
    {
        var name = ServiceCategoryNames.NameFor(x12Code)
            ?? throw new InvalidOperationException($"No category name for X12 service type {x12Code}.");
        return new ServiceCategoryMatch
        {
            ServiceTypeCode = name,
            ServiceTypeDescription = $"{name} (X12 service type {x12Code})",
            MatchedBy = "SystemDefault",
            MatchedRule = rule,
        };
    }

    /// <summary>
    /// Last-resort fallback for a professional claim: infer the benefit
    /// category from place of service. This keeps adjudication from failing
    /// entirely when mappings are incomplete, but logs a warning so the
    /// mapping gap gets fixed. The POS → X12 table is unchanged; the result is
    /// the named category (POS 11 → "98" → "Office Visit").
    /// </summary>
    private static ServiceCategoryMatch? InferFromPlaceOfService(string pos, string procedureCode)
    {
        var code = pos switch
        {
            "11" => "98",
            "21" or "22" or "23" => "48",
            "20" or "24" => "50",
            "31" or "32" => "86",
            "34" => "42",
            "51" or "52" or "53" or "54" => "86",
            "61" or "62" => "48",
            "71" or "72" => "A4",
            "81" => "35",
            _ => null
        };

        return code is null ? null : SystemDefault(code, $"POS-fallback:{pos}");
    }
}

/// <summary>
/// Repository interface for service category mappings.
/// Implementations can read from MongoDB, Cosmos DB, or QNXT extracts.
/// </summary>
public interface IServiceCategoryMappingRepository
{
    /// <summary>
    /// Get all mappings for a tenant, optionally filtered by plan.
    /// Pass null for planId to get tenant-level defaults.
    /// </summary>
    Task<IReadOnlyList<ServiceCategoryMapping>> GetMappingsAsync(
        string tenantId, Guid? benefitPlanId, CancellationToken ct = default);
}
