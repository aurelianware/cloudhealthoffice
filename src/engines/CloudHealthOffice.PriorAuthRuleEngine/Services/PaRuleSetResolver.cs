using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Persistence;

namespace CloudHealthOffice.PriorAuthRuleEngine.Services;

/// <summary>
/// Which stored rule set applies to a tenant's state, line of business and
/// program. The engine and the published list of PA-required services
/// (<see cref="PaRequiredServicesList"/>) both use it, so the list names
/// exactly the rules a request would be evaluated against.
///
/// Resolution hierarchy (most specific wins; the first non-empty set is used):
///   Tenant + State + LOB + Program  →  Platform + State + LOB + Program
///   → Tenant + State + LOB + any   →  Platform + State + LOB + any
/// </summary>
public static class PaRuleSetResolver
{
    /// <summary>
    /// The rule set that applies, and the key it was found under, with its
    /// enabled rules in evaluation order. The program id travels on the two
    /// program-specific candidates only: a state-and-line-of-business
    /// fallback is not one program's rule set, and saying it was would hide
    /// the fallback.
    /// </summary>
    public static async Task<(RuleSetKey? Key, IReadOnlyList<PaRuleDocument> Rules)> ResolveAsync(
        IPaRuleRepository repository, string stateCode, PaLineOfBusiness lob, string? program,
        string? tenantId, string? programId, CancellationToken ct)
    {
        var candidates = new[]
        {
            new RuleSetKey { StateCode = stateCode, Lob = lob, Program = program, TenantId = tenantId, ProgramId = programId },
            new RuleSetKey { StateCode = stateCode, Lob = lob, Program = program, TenantId = null,     ProgramId = programId },
            new RuleSetKey { StateCode = stateCode, Lob = lob, Program = null,    TenantId = tenantId },
            new RuleSetKey { StateCode = stateCode, Lob = lob, Program = null,    TenantId = null }
        };

        // Deduplicate — when TenantId is null or Program is null, some keys collapse.
        var seen = new HashSet<string>();

        foreach (var key in candidates)
        {
            var keyStr = key.ToString();
            if (!seen.Add(keyStr)) continue;

            var rules = (await repository.GetRulesAsync(key, ct))
                .Where(r => r.IsEnabled)
                .OrderBy(r => (int)r.Category)
                .ThenBy(r => r.Priority)
                .ThenBy(r => r.Scope == RuleScope.Platform ? 1 : 0) // tenant rules first
                .ToList();

            if (rules.Count > 0)
                return (key, rules);
        }

        return (null, Array.Empty<PaRuleDocument>());
    }
}
