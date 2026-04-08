using System.Diagnostics;
using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.PriorAuthRuleEngine.Services;

/// <summary>
/// Resolves the applicable rule set for a PA request and evaluates rules
/// in order until a decisive outcome is reached.
///
/// Evaluation order:
///   1. Resolve RuleSetKey from context (StateCode + Lob + Program + TenantId)
///   2. Load applicable rules from repository (Redis cache → Cosmos/Mongo)
///   3. Sort by Category (band) then Priority within band
///   4. Iterate — first non-null result short-circuits
///   5. Exhausted with no match → Pend
///
/// Rule resolution hierarchy (most specific wins):
///   Tenant + State + LOB + Program  →  Platform + State + LOB + Program
///   → Tenant + State + LOB + any   →  Platform + State + LOB + any
///
/// See PaRuleDocument.RuleType for the mapping from document to IPaRule impl.
/// </summary>
public sealed class PriorAuthRuleEngineService : IPriorAuthRuleEngine
{
    private readonly IPaRuleRepository _repository;
    private readonly IEnumerable<IPaRule> _ruleImpls;
    private readonly IProviderApprovalHistoryService? _providerHistory;
    private readonly IMemberAuthHistoryService? _memberHistory;
    private readonly PriorAuthRuleEngineOptions _opts;
    private readonly ILogger<PriorAuthRuleEngineService> _logger;

    // Keyed by RuleType string for fast dispatch
    private readonly IReadOnlyDictionary<string, IPaRule> _ruleRegistry;

    public PriorAuthRuleEngineService(
        IPaRuleRepository repository,
        IEnumerable<IPaRule> ruleImpls,
        IOptions<PriorAuthRuleEngineOptions> options,
        ILogger<PriorAuthRuleEngineService> logger,
        IProviderApprovalHistoryService? providerHistory = null,
        IMemberAuthHistoryService? memberHistory = null)
    {
        _repository      = repository;
        _ruleImpls       = ruleImpls;
        _opts            = options.Value;
        _logger          = logger;
        _providerHistory = providerHistory;
        _memberHistory   = memberHistory;

        _ruleRegistry = ruleImpls.ToDictionary(r => r.RuleType, StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation(
            "PriorAuthRuleEngine initialised with {Count} rule implementations: {Types}",
            _ruleRegistry.Count,
            string.Join(", ", _ruleRegistry.Keys));
    }

    // ── IPriorAuthRuleEngine ──────────────────────────────────────

    public async Task<PaRuleDecision> EvaluateAsync(
        PaRuleContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // Pre-fetch context that rules need — done once before evaluation starts
        context = await EnrichContextAsync(context, ct);

        // Resolve and load applicable rules
        var rules = await ResolveRulesAsync(context, ct);

        if (rules.Count == 0)
        {
            _logger.LogDebug(
                "No rules found for {State}/{Lob}/{Program} (tenant {Tenant}) — Pend",
                context.StateCode, context.Lob, context.Program ?? "any", context.TenantId);

            return Pend("NoRulesConfigured", context, sw);
        }

        _logger.LogDebug(
            "Evaluating {Count} rules for {State}/{Lob}/{Program}",
            rules.Count, context.StateCode, context.Lob, context.Program ?? "any");

        var evaluatedIds = new List<string>(rules.Count);

        foreach (var ruleDoc in rules)
        {
            ct.ThrowIfCancellationRequested();

            if (!ruleDoc.IsEnabled) continue;

            if (!_ruleRegistry.TryGetValue(ruleDoc.RuleType, out var impl))
            {
                _logger.LogWarning(
                    "No implementation registered for RuleType '{RuleType}' (rule {RuleId}) — skipping",
                    ruleDoc.RuleType, ruleDoc.RuleId);
                continue;
            }

            evaluatedIds.Add(ruleDoc.RuleId);

            PaRuleDecision? decision;
            try
            {
                decision = await impl.EvaluateAsync(ruleDoc, context, ct);
            }
            catch (Exception ex) when (_opts.PendOnRuleError)
            {
                _logger.LogError(ex,
                    "Rule {RuleId} ({RuleType}) threw during evaluation — Pending per PendOnRuleError=true",
                    ruleDoc.RuleId, ruleDoc.RuleType);

                return Pend($"RuleError:{ruleDoc.RuleId}", context, sw) with
                {
                    EvaluatedRules = evaluatedIds
                };
            }

            if (decision is null) continue; // no match — try next rule

            // Decision reached
            sw.Stop();
            _logger.LogInformation(
                "PA rule engine: {Outcome} via rule {RuleId} for {State}/{Lob}/{Program} " +
                "after evaluating {Count} rules in {Ms}ms",
                decision.Outcome, ruleDoc.RuleId,
                context.StateCode, context.Lob, context.Program ?? "any",
                evaluatedIds.Count, sw.ElapsedMilliseconds);

            return decision with
            {
                EvaluatedRules = evaluatedIds,
                ElapsedMs      = sw.ElapsedMilliseconds
            };
        }

        // All rules exhausted — Pend for clinical review
        return Pend("NoRuleMatch", context, sw) with { EvaluatedRules = evaluatedIds };
    }

    public async Task<IReadOnlyList<PaRuleDocument>> GetApplicableRulesAsync(
        RuleSetKey key, CancellationToken ct = default)
    {
        return await _repository.GetRulesAsync(key, ct);
    }

    // ── Context enrichment ────────────────────────────────────────

    private async Task<PaRuleContext> EnrichContextAsync(
        PaRuleContext context, CancellationToken ct)
    {
        ProviderApprovalHistory? history  = null;
        MemberAuthHistory? memberHistory  = null;

        // Provider history — needed for gold card rules
        if (_providerHistory is not null && !string.IsNullOrEmpty(context.ServicingProviderNpi))
        {
            try
            {
                history = await _providerHistory.GetAsync(
                    context.ServicingProviderNpi, _opts.GoldCardLookbackDays, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch provider history for NPI {Npi}",
                    context.ServicingProviderNpi);
            }
        }

        // Member auth history — needed for quantity limit rules
        if (_memberHistory is not null)
        {
            var benefitPeriod = context.ServiceDate.Year.ToString();
            try
            {
                memberHistory = await _memberHistory.GetAsync(
                    context.MemberId, context.ProcedureCodes, benefitPeriod, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch member auth history for {MemberId}",
                    context.MemberId);
            }
        }

        return context with
        {
            ProviderHistory = history ?? context.ProviderHistory,
            MemberHistory   = memberHistory ?? context.MemberHistory
        };
    }

    // ── Rule resolution ───────────────────────────────────────────

    private async Task<IReadOnlyList<PaRuleDocument>> ResolveRulesAsync(
        PaRuleContext context, CancellationToken ct)
    {
        // Try resolution hierarchy from most to least specific.
        // Return the first non-empty rule set found.

        var candidates = new[]
        {
            new RuleSetKey { StateCode = context.StateCode, Lob = context.Lob, Program = context.Program, TenantId = context.TenantId },
            new RuleSetKey { StateCode = context.StateCode, Lob = context.Lob, Program = context.Program, TenantId = null },
            new RuleSetKey { StateCode = context.StateCode, Lob = context.Lob, Program = null,            TenantId = context.TenantId },
            new RuleSetKey { StateCode = context.StateCode, Lob = context.Lob, Program = null,            TenantId = null }
        };

        // Deduplicate — when TenantId is null or Program is null, some keys collapse.
        // Return the first non-empty rule set (most specific wins).
        var seen = new HashSet<string>();

        foreach (var key in candidates)
        {
            var keyStr = key.ToString();
            if (!seen.Add(keyStr)) continue;

            var rules = (await _repository.GetRulesAsync(key, ct))
                .Where(r => r.IsEnabled)
                .OrderBy(r => (int)r.Category)
                .ThenBy(r => r.Priority)
                .ThenBy(r => r.Scope == RuleScope.Platform ? 1 : 0) // tenant rules first
                .ToList();

            if (rules.Count > 0)
                return rules;
        }

        return Array.Empty<PaRuleDocument>();
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static PaRuleDecision Pend(string reason, PaRuleContext ctx, Stopwatch sw) => new()
    {
        Outcome            = PaDecisionOutcome.Pend,
        FiringRuleId       = reason,
        FiringRuleName     = reason,
        ResolvedRuleSetKey = $"{ctx.TenantId ?? "platform"}/{ctx.StateCode}/{ctx.Lob}/{ctx.Program ?? "any"}",
        ElapsedMs          = sw.ElapsedMilliseconds
    };
}
