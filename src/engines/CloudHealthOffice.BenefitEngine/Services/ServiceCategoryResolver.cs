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
/// <see cref="IServiceCategoryResolver.ResolveAsync"/>. On an 837I the
/// claim's place-of-service slot carries CLM05-1, the facility type code
/// (the first two digits of the type of bill), not a CMS place-of-service
/// code: "11" is a hospital inpatient bill, not an office. The resolver
/// therefore needs to know whether the claim is institutional before it
/// infers anything from that value.
/// </summary>
/// <param name="ClaimType">"837P", "837I" or "837D" (the engine's claim type), or null.</param>
/// <param name="TypeOfBill">
/// NUBC type of bill, three characters (facility type + frequency, e.g.
/// "111") or four with the leading zero ("0111"); null when unknown.
/// </param>
public sealed record ServiceCategoryClaimContext(string? ClaimType, string? TypeOfBill)
{
    /// <summary>An institutional claim: claim type 837I, or a type of bill is present.</summary>
    public bool IsInstitutional =>
        string.Equals(ClaimType?.Trim(), "837I", StringComparison.OrdinalIgnoreCase)
        || string.Equals(ClaimType?.Trim(), "Institutional", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrWhiteSpace(TypeOfBill);
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
        //    type of bill / facility type and the revenue code — never from
        //    place of service, which on an 837I holds CLM05-1 (facility type
        //    "11" = hospital inpatient, not POS 11 = office). Professional
        //    claims: infer the broad category from place of service.
        if (claim is { IsInstitutional: true })
        {
            var institutional = InferInstitutional(claim.TypeOfBill, placeOfService, revenueCode);
            if (institutional is not null)
            {
                _logger.LogWarning(
                    "No explicit mapping for institutional {CodeType} {ProcedureCode} REV {RevenueCode} — " +
                    "falling back to type-of-bill / revenue-code inference ({Rule}): {ServiceTypeCode}",
                    codeType, procedureCode, revenueCode, institutional.MatchedRule, institutional.ServiceTypeCode);
                return institutional;
            }

            _logger.LogError(
                "No service category mapping found for institutional {CodeType} {ProcedureCode} REV {RevenueCode} TOB {TypeOfBill}",
                codeType, procedureCode, revenueCode, claim.TypeOfBill);
            return null;
        }

        var fallback = InferFromPlaceOfService(placeOfService, procedureCode);
        if (fallback is not null)
        {
            _logger.LogWarning(
                "No explicit mapping for {CodeType} {ProcedureCode} POS {POS} — " +
                "falling back to POS-based inference: {ServiceTypeCode}",
                codeType, procedureCode, placeOfService, fallback.ServiceTypeCode);
            return fallback;
        }

        _logger.LogError(
            "No service category mapping found for {CodeType} {ProcedureCode} POS {POS}",
            codeType, procedureCode, placeOfService);
        return null;
    }

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
    /// type "11"); without a type of bill it is the place-of-service value,
    /// which on an 837I carries CLM05-1, the same two digits.
    /// <list type="number">
    ///   <item>An inpatient bill (11x/12x hospital, 18x/28x swing bed,
    ///     21x/22x SNF, 41x, 81x/82x hospice) is one stay under one benefit:
    ///     every line takes the facility's category, whatever its revenue code
    ///     (an ER visit that led to the admission is part of the stay).</item>
    ///   <item>Otherwise the line's revenue code decides when it is specific:
    ///     045x emergency room, 010x–021x accommodation (inpatient).</item>
    ///   <item>Otherwise the outpatient / other facility type: 13x/14x/43x
    ///     hospital outpatient, 85x critical access hospital outpatient,
    ///     32x–34x home health.</item>
    /// </list>
    /// Returns null when nothing is recognized (CARC 204 upstream) rather than
    /// guessing a professional category.
    /// <para>
    /// Categories are the named benefit categories plans are authored with
    /// (the vocabulary of <c>schemas/service-category-mappings/system-defaults.json</c>:
    /// "Inpatient Hospital", "Emergency Room", "Skilled Nursing", "Home Health"),
    /// since the engine matches a plan benefit by exact service type code.
    /// </para>
    /// </summary>
    private static ServiceCategoryMatch? InferInstitutional(string? typeOfBill, string placeOfService, string? revenueCode)
    {
        var facilityType = FacilityTypeFromBill(typeOfBill);
        var facilitySource = "TOB";
        if (facilityType is null && placeOfService is { Length: 2 } && char.IsDigit(placeOfService[0]) && char.IsDigit(placeOfService[1]))
        {
            facilityType = placeOfService;
            facilitySource = "FacilityType";
        }

        var (inpatientCode, inpatientDesc) = facilityType switch
        {
            "11" or "12" or "41" => (InpatientHospital, "Inpatient hospital (type of bill 11x/12x/41x)"),
            "18" or "21" or "22" or "28" => (SkilledNursing, "Skilled nursing / swing bed (type of bill 18x/21x/22x/28x)"),
            "81" or "82" => (Hospice, "Hospice (type of bill 81x/82x)"),
            _ => ((string?)null, (string?)null),
        };
        if (inpatientCode is not null)
            return SystemDefault(inpatientCode, inpatientDesc!, $"{facilitySource}-fallback:{facilityType}");

        var revenue = NormalizeRevenueCode(revenueCode);
        if (revenue is not null && int.TryParse(revenue, out var rev))
        {
            if (rev is >= 450 and <= 459)
                return SystemDefault(EmergencyRoom, "Emergency room (revenue code 045x)", $"REV-fallback:{revenue}");
            if (rev is >= 100 and <= 219)
                return SystemDefault(InpatientHospital, "Inpatient accommodation (revenue code 0100-0219)", $"REV-fallback:{revenue}");
        }

        var (code, desc) = facilityType switch
        {
            "13" or "14" or "43" or "85" => (OutpatientHospital, "Outpatient hospital (type of bill 13x/14x/43x/85x)"),
            "32" or "33" or "34" => (HomeHealth, "Home health (type of bill 32x-34x)"),
            _ => ((string?)null, (string?)null),
        };
        return code is null ? null : SystemDefault(code, desc!, $"{facilitySource}-fallback:{facilityType}");
    }

    /// <summary>Institutional fallback category: inpatient hospital stay.</summary>
    public const string InpatientHospital = "Inpatient Hospital";
    /// <summary>Institutional fallback category: hospital outpatient.</summary>
    public const string OutpatientHospital = "Outpatient Hospital";
    /// <summary>Institutional fallback category: emergency room (revenue code 045x).</summary>
    public const string EmergencyRoom = "Emergency Room";
    /// <summary>Institutional fallback category: skilled nursing facility / swing bed.</summary>
    public const string SkilledNursing = "Skilled Nursing";
    /// <summary>Institutional fallback category: home health agency.</summary>
    public const string HomeHealth = "Home Health";
    /// <summary>Institutional fallback category: hospice.</summary>
    public const string Hospice = "Hospice";

    /// <summary>Facility type (first two digits) of a NUBC type of bill: "111" or "0111" → "11".</summary>
    private static string? FacilityTypeFromBill(string? typeOfBill)
    {
        if (string.IsNullOrWhiteSpace(typeOfBill)) return null;
        var tob = typeOfBill.Trim();
        if (tob.Length == 4 && tob[0] == '0') tob = tob[1..];
        return tob.Length >= 2 && char.IsDigit(tob[0]) && char.IsDigit(tob[1]) ? tob[..2] : null;
    }

    private static ServiceCategoryMatch SystemDefault(string code, string description, string rule) => new()
    {
        ServiceTypeCode = code,
        ServiceTypeDescription = description,
        MatchedBy = "SystemDefault",
        MatchedRule = rule,
    };

    /// <summary>
    /// Last-resort fallback: infer benefit category from place of service.
    /// This keeps adjudication from failing entirely when mappings are incomplete,
    /// but logs a warning so the mapping gap gets fixed.
    /// </summary>
    private static ServiceCategoryMatch? InferFromPlaceOfService(string pos, string procedureCode)
    {
        var (code, desc) = pos switch
        {
            "11" => ("98", "Professional (Physician) Visit - Office"),
            "21" or "22" or "23" => ("48", "Hospital - Inpatient"),
            "20" or "24" => ("50", "Hospital - Outpatient"),
            "31" or "32" => ("86", "Emergency Services"),
            "34" => ("42", "Home Health Care"),
            "51" or "52" or "53" or "54" => ("86", "Emergency Services"),
            "61" or "62" => ("48", "Hospital - Inpatient"),
            "71" or "72" => ("A4", "Psychiatric"),
            "81" => ("35", "Dental Care"),
            _ => (null, null)
        };

        if (code is null) return null;

        return new ServiceCategoryMatch
        {
            ServiceTypeCode = code,
            ServiceTypeDescription = desc!,
            MatchedBy = "SystemDefault",
            MatchedRule = $"POS-fallback:{pos}"
        };
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
