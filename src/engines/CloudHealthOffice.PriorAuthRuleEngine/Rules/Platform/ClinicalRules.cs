using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Rules;

namespace CloudHealthOffice.PriorAuthRuleEngine.Rules.Platform;

/// <summary>
/// Procedure Requires Authorization — the most common clinical criteria rule.
///
/// Signals that one or more of the requested procedure codes require PA for
/// this state/LOB/program. When matched and no exemption has already fired,
/// the outcome depends on the denial config:
///   - DenialCode set → Deny (procedure categorically requires clinical review
///     before PA is granted — engine denies and routes to manual review)
///   - DenialCode null → Pend (route to clinical review queue)
///
/// Most ClinicalCriteria rules should Pend rather than Deny outright.
/// Deny is appropriate when the procedure is explicitly excluded (non-covered).
///
/// RuleType:  "ProcedureRequiresAuth"
/// Category:  ClinicalCriteria (band 1)
/// </summary>
public sealed class ProcedureRequiresAuthRule : PaRuleBase
{
    public override string RuleType       => "ProcedureRequiresAuth";
    public override RuleCategory Category => RuleCategory.ClinicalCriteria;
    public override int Priority          => 10;

    public override Task<PaRuleDecision?> EvaluateAsync(
        PaRuleDocument config,
        PaRuleContext context,
        CancellationToken ct = default)
    {
        if (!AppliesToProcedures(config, context.ProcedureCodes))
            return Task.FromResult<PaRuleDecision?>(null);

        if (!MatchesPlaceOfService(config, context.PlaceOfServiceCode))
            return Task.FromResult<PaRuleDecision?>(null);

        // Deny = non-covered / categorically excluded
        // Pend = needs clinical review (no DenialCode configured)
        var outcome = !string.IsNullOrEmpty(config.DenialCode)
            ? Deny(config)
            : new PaRuleDecision
            {
                Outcome            = PaDecisionOutcome.Pend,
                FiringRuleId       = config.RuleId,
                FiringRuleName     = config.RuleName,
                ResolvedRuleSetKey = $"{config.TenantId ?? "platform"}/{config.StateCode}/{config.Lob}/{config.Program ?? "any"}"
            };

        return Task.FromResult<PaRuleDecision?>(outcome);
    }
}

// ─────────────────────────────────────────────────────────────────

/// <summary>
/// Quantity Limit — auto-approve when within the benefit period limit;
/// Pend when the member has exhausted their allowed units/visits.
///
/// Examples:
///   TX STAR: chiropractic visits ≤ 20/year → auto-approve; > 20 → Pend
///   TX STAR: PT/OT visits ≤ 30/year → auto-approve; > 30 → Pend
///
/// RuleType:  "QuantityLimit"
/// Category:  QuantityLimit (band 2)
///
/// Config fields used:
///   UnitLimit   — max units per benefit period (null = no unit limit)
///   VisitLimit  — max visits per benefit period (null = no visit limit)
///   ProcedureCodes / Prefixes — scope to specific procedure types
/// </summary>
public sealed class QuantityLimitRule : PaRuleBase
{
    public override string RuleType       => "QuantityLimit";
    public override RuleCategory Category => RuleCategory.QuantityLimit;
    public override int Priority          => 20;

    public override Task<PaRuleDecision?> EvaluateAsync(
        PaRuleDocument config,
        PaRuleContext context,
        CancellationToken ct = default)
    {
        if (!AppliesToProcedures(config, context.ProcedureCodes))
            return Task.FromResult<PaRuleDecision?>(null);

        // Within unit limit — auto-approve
        if (config.UnitLimit.HasValue && context.MemberHistory is not null)
        {
            var projectedUnits = context.MemberHistory.AuthorisedUnits + context.RequestedUnits;
            if (projectedUnits <= config.UnitLimit.Value)
                return Task.FromResult<PaRuleDecision?>(Approve(config));
        }

        // Within visit limit — auto-approve
        if (config.VisitLimit.HasValue && context.MemberHistory is not null)
        {
            // Each PA request = 1 visit in the context of this rule
            var projectedVisits = context.MemberHistory.AuthorisedVisits + 1;
            if (projectedVisits <= config.VisitLimit.Value)
                return Task.FromResult<PaRuleDecision?>(Approve(config));
        }

        // If no limits configured, this rule is a pass-through
        if (!config.UnitLimit.HasValue && !config.VisitLimit.HasValue)
            return Task.FromResult<PaRuleDecision?>(null);

        // Over limit — Pend for clinical review
        return Task.FromResult<PaRuleDecision?>(new PaRuleDecision
        {
            Outcome            = PaDecisionOutcome.Pend,
            FiringRuleId       = config.RuleId,
            FiringRuleName     = config.RuleName,
            ResolvedRuleSetKey = $"{config.TenantId ?? "platform"}/{config.StateCode}/{config.Lob}/{config.Program ?? "any"}",
            DenialReason       = $"Quantity limit reached: member has used " +
                                 $"{context.MemberHistory?.AuthorisedVisits ?? 0} of " +
                                 $"{config.VisitLimit} allowed visits this benefit period."
        });
    }
}

// ─────────────────────────────────────────────────────────────────

/// <summary>
/// Diagnosis Required — PA is only necessary when specific ICD-10 codes
/// are present. Without the required diagnosis, the procedure is auto-approved.
///
/// Examples:
///   TX STAR: CPAP/BiPAP (E0601) requires diagnosis of sleep apnea (G47.3x)
///   TX STARPlus: power wheelchair requires G/M diagnosis codes
///
/// RuleType:  "DiagnosisRequired"
/// Category:  DiagnosisRequired (band 3)
///
/// Config fields used:
///   RequiredDiagnosisCodes — ICD-10 codes that trigger PA requirement
///   ProcedureCodes / Prefixes — scope to specific procedure types
/// </summary>
public sealed class DiagnosisRequiredRule : PaRuleBase
{
    public override string RuleType       => "DiagnosisRequired";
    public override RuleCategory Category => RuleCategory.DiagnosisRequired;
    public override int Priority          => 30;

    public override Task<PaRuleDecision?> EvaluateAsync(
        PaRuleDocument config,
        PaRuleContext context,
        CancellationToken ct = default)
    {
        if (!AppliesToProcedures(config, context.ProcedureCodes))
            return Task.FromResult<PaRuleDecision?>(null);

        // Required diagnosis present — PA is required, route to clinical review
        if (HasRequiredDiagnosis(config, context.DiagnosisCodes))
            return Task.FromResult<PaRuleDecision?>(new PaRuleDecision
            {
                Outcome            = PaDecisionOutcome.Pend,
                FiringRuleId       = config.RuleId,
                FiringRuleName     = config.RuleName,
                ResolvedRuleSetKey = $"{config.TenantId ?? "platform"}/{config.StateCode}/{config.Lob}/{config.Program ?? "any"}",
                DenialReason       = "Procedure requires prior authorization with the submitted diagnosis."
            });

        // Diagnosis not present — procedure does not need PA in this context
        return Task.FromResult<PaRuleDecision?>(Approve(config));
    }
}

// ─────────────────────────────────────────────────────────────────

/// <summary>
/// Provider Type Exemption — certain procedure codes do not require PA
/// when rendered by a specific taxonomy / provider type.
///
/// Examples:
///   TX STAR: primary care visits (99201–99215) by PCPs (207Q*, 207R*)
///            are exempt from PA regardless of diagnosis
///   TX STARPlus: home health by certified agencies (251E*) exempt for
///            the first 5 visits
///
/// RuleType:  "ProviderTypeExemption"
/// Category:  ProviderType (band 6)
///
/// Config fields used:
///   ExemptTaxonomyPrefixes — taxonomy code prefixes that qualify for exemption
///   ProcedureCodes / Prefixes — scope to specific procedure types
/// </summary>
public sealed class ProviderTypeExemptionRule : PaRuleBase
{
    public override string RuleType       => "ProviderTypeExemption";
    public override RuleCategory Category => RuleCategory.ProviderType;
    public override int Priority          => 60;

    public override Task<PaRuleDecision?> EvaluateAsync(
        PaRuleDocument config,
        PaRuleContext context,
        CancellationToken ct = default)
    {
        if (!AppliesToProcedures(config, context.ProcedureCodes))
            return Task.FromResult<PaRuleDecision?>(null);

        if (config.ExemptTaxonomyPrefixes.Count == 0)
            return Task.FromResult<PaRuleDecision?>(null);

        var taxonomy = context.ServicingProviderTaxonomy;
        if (string.IsNullOrEmpty(taxonomy))
            return Task.FromResult<PaRuleDecision?>(null);

        var isExempt = config.ExemptTaxonomyPrefixes.Any(prefix =>
            taxonomy.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult<PaRuleDecision?>(isExempt ? Approve(config) : null);
    }
}
