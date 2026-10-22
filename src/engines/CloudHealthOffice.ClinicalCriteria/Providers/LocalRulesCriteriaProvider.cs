using CloudHealthOffice.ClinicalCriteria.Abstractions;
using CloudHealthOffice.ClinicalCriteria.Configuration;
using CloudHealthOffice.ClinicalCriteria.Domain;
using CloudHealthOffice.ClinicalCriteria.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;

namespace CloudHealthOffice.ClinicalCriteria.Providers;

/// <summary>
/// The in-repo provider: Cloud Health Office's own configured rules, presented
/// through the criteria seam.
///
/// It is not a substitute for InterQual or MCG and does not claim to be. The
/// rules it runs are coverage and benefit rules — does this procedure require
/// authorization here, is the member inside the visit limit — not clinical
/// guidelines. What it gives the platform is a provider that is always
/// present and always configured, so the seam is exercised in every
/// environment and no deployment depends on a licence to function.
///
/// The mapping is deliberately conservative. An approving rule means the
/// request needs no clinical review, which reads as meeting criteria. A
/// denying rule is a coverage exclusion, so it maps to
/// <see cref="CriteriaRecommendation.NotCovered"/> rather than to a clinical
/// "does not meet" — these rules are not competent to make a medical-necessity
/// determination, and dressing one up as clinical would be the one mistake
/// this seam exists to prevent.
/// </summary>
public sealed class LocalRulesCriteriaProvider(IPriorAuthRuleEngine ruleEngine) : IClinicalCriteriaProvider
{
    public string ProviderId => ClinicalCriteriaOptions.LocalRulesProviderId;

    public string DisplayName => "Cloud Health Office rules (no licensed clinical content)";

    /// <summary>Always true: the rule engine ships with the platform.</summary>
    public bool IsConfigured => true;

    public async Task<CriteriaEvaluation> EvaluateAsync(CriteriaRequest request, CancellationToken ct = default)
    {
        var decision = await ruleEngine.EvaluateAsync(ToRuleContext(request), ct).ConfigureAwait(false);

        var (recommendation, rationale) = decision.Outcome switch
        {
            PaDecisionOutcome.Approve => (
                CriteriaRecommendation.MeetsCriteria,
                new List<string> { $"No clinical review required by rule '{decision.FiringRuleName}'." }),

            PaDecisionOutcome.Deny => (
                CriteriaRecommendation.NotCovered,
                new List<string>
                {
                    $"Excluded by rule '{decision.FiringRuleName}'" +
                    (decision.DenialCode is null ? "." : $" ({decision.DenialCode})."),
                    decision.DenialReason ?? "No reason recorded on the rule.",
                }),

            _ => (
                CriteriaRecommendation.Indeterminate,
                new List<string>
                {
                    "Cloud Health Office rules reached no conclusion; clinical review is required.",
                    $"Rule set evaluated: {decision.ResolvedRuleSetKey}.",
                }),
        };

        return new CriteriaEvaluation
        {
            ProviderId = ProviderId,
            ProviderDisplayName = DisplayName,
            Recommendation = recommendation,
            FailureReason = recommendation == CriteriaRecommendation.Indeterminate
                ? "no-local-rule-conclusive"
                : null,
            Guideline = new CriteriaGuidelineReference
            {
                GuidelineId = decision.FiringRuleId,
                Title = decision.FiringRuleName,
                Version = decision.ResolvedRuleSetKey,
            },
            Rationale = rationale,
            OutstandingQuestions = recommendation == CriteriaRecommendation.Indeterminate
                ? [new CriteriaQuestion
                    {
                        Id = "clinical-review",
                        Text = "Does the submitted clinical documentation support medical necessity for the requested service?",
                    }]
                : [],
            EvaluatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Narrows a criteria request to what the rule engine takes. Level of
    /// care, stay day and the clinical indicators have no counterpart there —
    /// they are exactly the inputs a licensed guideline uses and local rules
    /// do not, which is the gap this seam is built to close.
    /// </summary>
    private static PaRuleContext ToRuleContext(CriteriaRequest request) => new()
    {
        TenantId                  = request.TenantId,
        StateCode                 = request.StateCode,
        Lob                       = request.Lob,
        Program                   = request.Program,
        RequestingProviderNpi     = request.RequestingProviderNpi,
        ServicingProviderNpi      = request.ServicingProviderNpi,
        ServicingProviderTaxonomy = request.ServicingProviderTaxonomy,
        MemberId                  = request.MemberId,
        MemberDateOfBirth         = request.MemberDateOfBirth,
        ServiceDate               = request.ServiceDate,
        ProcedureCodes            = request.ProcedureCodes,
        DiagnosisCodes            = request.DiagnosisCodes,
        PlaceOfServiceCode        = request.PlaceOfServiceCode,
        EstimatedCost             = request.EstimatedCost,
        RequestedUnits            = (int)Math.Ceiling(request.RequestedUnits),
    };
}
