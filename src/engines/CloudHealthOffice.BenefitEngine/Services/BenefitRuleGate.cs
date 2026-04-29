using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.BenefitEngine.Services;

/// <summary>
/// Selects the applicable <see cref="BenefitCategoryConfig"/> for a
/// claim line given the resolved service-type code (capability BP 5.10).
///
/// <para>
/// The plan projection may carry multiple benefits with the same
/// <c>ServiceTypeCode</c> — for example a plan that authors a
/// "Pediatric Office Visit (age 0-17)" and an "Adult Office Visit
/// (age 18+)" benefit, both projecting to service-type code <c>98</c>.
/// The gate walks those candidates in declaration order and returns
/// the first whose <see cref="BenefitCategoryConfig.Predicate"/> is
/// satisfied for the supplied <see cref="MemberContext"/>.
/// </para>
///
/// <para>
/// Posture (Decision 3): when <see cref="MemberContext"/> is null the
/// gate <b>skips predicate evaluation</b> and returns the first
/// candidate as-is. This is the best-effort posture — predicates only
/// gate adjudication when the caller chose to supply context. A
/// non-null context activates strict evaluation; predicates that
/// can't be satisfied with the supplied context fail closed.
/// </para>
/// </summary>
public interface IBenefitRuleGate
{
    /// <summary>
    /// Returns the first <see cref="BenefitCategoryConfig"/> whose
    /// service-type code matches <paramref name="serviceTypeCode"/>
    /// AND whose predicate is satisfied for the supplied member
    /// encounter, or <c>null</c> when every candidate's predicate
    /// rejects.
    /// </summary>
    BenefitCategoryConfig? PickApplicable(
        BenefitPlanConfig plan,
        string serviceTypeCode,
        BenefitResolutionRequest request,
        ClaimLineInput? line);
}

public sealed class BenefitRuleGate : IBenefitRuleGate
{
    private readonly ILogger<BenefitRuleGate> _logger;

    public BenefitRuleGate(ILogger<BenefitRuleGate> logger)
    {
        _logger = logger;
    }

    public BenefitCategoryConfig? PickApplicable(
        BenefitPlanConfig plan,
        string serviceTypeCode,
        BenefitResolutionRequest request,
        ClaimLineInput? line)
    {
        var candidates = plan.GetCategories(serviceTypeCode);
        if (candidates.Count == 0)
        {
            return null;
        }

        var memberContext = request.Member;

        // Decision 3 best-effort: when MemberContext is null, skip
        // predicate evaluation entirely and return the first candidate.
        // Emit the no-context counter once per call when at least one
        // candidate carries a predicate so operators see the per-tenant
        // signal.
        if (memberContext is null)
        {
            if (candidates.Any(c => c.Predicate is not null))
            {
                BenefitEngineMetrics.PredicateSkippedNoMemberContext.Add(1,
                    new KeyValuePair<string, object?>("cho.tenant_id", plan.TenantId),
                    new KeyValuePair<string, object?>("cho.service_type_code", serviceTypeCode));
            }
            return candidates[0];
        }

        var evalContext = BuildEvaluationContext(memberContext, line);

        foreach (var candidate in candidates)
        {
            if (candidate.Predicate is null)
            {
                // No predicate ⇒ unconditionally applicable.
                return candidate;
            }

            if (candidate.Predicate.Evaluate(evalContext))
            {
                return candidate;
            }
        }

        _logger.LogInformation(
            "BenefitRuleGate: no candidate predicate satisfied for plan {PlanId} serviceTypeCode={ServiceTypeCode} ({CandidateCount} candidates)",
            plan.Id, serviceTypeCode, candidates.Count);

        return null;
    }

    private static BenefitRuleEvaluationContext BuildEvaluationContext(
        MemberContext member,
        ClaimLineInput? line)
    {
        // When the request didn't carry diagnoses but the line under
        // adjudication does, fall back to the line-level diagnosis
        // codes so a single-dx claim still gates correctly.
        IReadOnlyCollection<string>? dx = member.DiagnosisCodes;
        if ((dx is null || dx.Count == 0) && line is not null && line.DiagnosisCodes.Count > 0)
        {
            dx = line.DiagnosisCodes;
        }

        return new BenefitRuleEvaluationContext
        {
            MemberAgeYears = member.AgeYears,
            MemberGender = member.Gender,
            DiagnosisCodes = dx,
        };
    }
}
