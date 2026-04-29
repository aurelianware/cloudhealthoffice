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
        CancellationToken ct = default);
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

        // 3. System-level fallback — use place of service to infer broad category
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

    private static bool RuleMatches(
        ProcedureCodeRule rule,
        string procedureCode,
        string codeType,
        string placeOfService,
        IReadOnlyList<string> modifiers,
        string? revenueCode)
    {
        // Code type must match
        if (!string.Equals(rule.CodeType, codeType, StringComparison.OrdinalIgnoreCase))
            return false;

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
