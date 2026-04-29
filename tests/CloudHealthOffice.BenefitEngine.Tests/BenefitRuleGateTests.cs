using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.BenefitEngine.Tests;

/// <summary>
/// Capability BP 5.10 — contract tests for <see cref="BenefitRuleGate"/>.
/// Pins the Decision 3 best-effort posture (null MemberContext skips
/// predicate evaluation), the predicate-driven selection of one of
/// several benefits sharing a ServiceTypeCode, and the all-rejected
/// null-return path the engine maps to a 96 denial.
/// </summary>
public class BenefitRuleGateTests
{
    private static BenefitPlanConfig PlanWith(params BenefitCategoryConfig[] categories) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = "tenant-a",
        PlanName = "Test",
        Categories = categories.ToList(),
    };

    private static BenefitCategoryConfig Cat(
        string code = "98",
        string description = "Office Visit",
        BenefitRulePredicate? predicate = null) => new()
    {
        ServiceTypeCode = code,
        ServiceTypeDescription = description,
        IsCovered = true,
        Predicate = predicate,
    };

    private static BenefitResolutionRequest Request(
        Guid planId,
        MemberContext? member = null,
        params string[] dx) => new()
    {
        MemberId = "MBR-001",
        SubscriberId = "SUB-001",
        BenefitPlanId = planId,
        ServiceDate = new DateOnly(2026, 4, 1),
        ClaimId = Guid.NewGuid().ToString(),
        Member = member,
        Lines =
        [
            new ClaimLineInput
            {
                LineNumber = 1,
                ProcedureCode = "99213",
                PlaceOfService = "11",
                BilledAmount = 200m,
                DiagnosisCodes = dx.ToList(),
            }
        ],
    };

    private static IBenefitRuleGate Gate() =>
        new BenefitRuleGate(NullLogger<BenefitRuleGate>.Instance);

    [Fact]
    public void NullMemberContext_ReturnsFirstCandidate_RegardlessOfPredicates()
    {
        var first = Cat(predicate: new BenefitRulePredicate { MemberAgeMin = 0, MemberAgeMax = 17 });
        var second = Cat(predicate: new BenefitRulePredicate { MemberAgeMin = 18 });
        var plan = PlanWith(first, second);
        var request = Request(plan.Id, member: null);

        var result = Gate().PickApplicable(plan, "98", request, request.Lines[0]);

        Assert.Same(first, result.Selected);
        Assert.Equal(2, result.CandidateCount);
    }

    [Fact]
    public void NoPredicates_ReturnsFirstCandidate()
    {
        var first = Cat();
        var second = Cat();
        var plan = PlanWith(first, second);
        var request = Request(plan.Id, member: new MemberContext { AgeYears = 30 });

        var result = Gate().PickApplicable(plan, "98", request, request.Lines[0]);

        Assert.Same(first, result.Selected);
        Assert.Equal(2, result.CandidateCount);
    }

    [Fact]
    public void TwoCandidatesSameCode_PredicateSelectsSecond()
    {
        var pediatric = Cat(description: "Pediatric Office Visit",
            predicate: new BenefitRulePredicate { MemberAgeMin = 0, MemberAgeMax = 17 });
        var adult = Cat(description: "Adult Office Visit",
            predicate: new BenefitRulePredicate { MemberAgeMin = 18 });
        var plan = PlanWith(pediatric, adult);
        var request = Request(plan.Id, member: new MemberContext { AgeYears = 42 });

        var result = Gate().PickApplicable(plan, "98", request, request.Lines[0]);

        Assert.Same(adult, result.Selected);
        Assert.Equal(2, result.CandidateCount);
    }

    [Fact]
    public void AllPredicatesReject_ReturnsNullSelectedWithCandidateCount()
    {
        var pediatric = Cat(predicate: new BenefitRulePredicate { MemberAgeMin = 0, MemberAgeMax = 17 });
        var senior = Cat(predicate: new BenefitRulePredicate { MemberAgeMin = 65 });
        var plan = PlanWith(pediatric, senior);
        var request = Request(plan.Id, member: new MemberContext { AgeYears = 42 });

        var result = Gate().PickApplicable(plan, "98", request, request.Lines[0]);

        Assert.Null(result.Selected);
        Assert.Equal(2, result.CandidateCount);
    }

    [Fact]
    public void NoCandidates_ReturnsNullSelectedWithZeroCandidateCount()
    {
        var plan = PlanWith();
        var request = Request(plan.Id, member: new MemberContext { AgeYears = 42 });

        var result = Gate().PickApplicable(plan, "98", request, line: null);

        Assert.Null(result.Selected);
        Assert.Equal(0, result.CandidateCount);
    }

    [Fact]
    public void NullPredicateAlongsidePredicates_NullPredicateWinsByOrder()
    {
        // First candidate has no predicate → unconditionally applicable.
        var unconditional = Cat();
        var pediatric = Cat(predicate: new BenefitRulePredicate { MemberAgeMin = 0, MemberAgeMax = 17 });
        var plan = PlanWith(unconditional, pediatric);
        var request = Request(plan.Id, member: new MemberContext { AgeYears = 42 });

        var result = Gate().PickApplicable(plan, "98", request, request.Lines[0]);

        Assert.Same(unconditional, result.Selected);
    }

    [Fact]
    public void DxFallbackToLine_WhenMemberContextHasNoDx()
    {
        var maternity = Cat(description: "Maternity",
            predicate: new BenefitRulePredicate
            {
                RequiredDiagnosisCodes = new List<string> { "Z34.00" }
            });
        var plan = PlanWith(maternity);
        var request = Request(plan.Id,
            member: new MemberContext { AgeYears = 28 },
            dx: "Z34.00");

        var result = Gate().PickApplicable(plan, "98", request, request.Lines[0]);

        Assert.Same(maternity, result.Selected);
    }

    [Fact]
    public void GenderPredicate_RejectsMismatchedMember()
    {
        var female = Cat(description: "Maternity",
            predicate: new BenefitRulePredicate { MemberGender = BenefitMemberGender.Female });
        var plan = PlanWith(female);
        var request = Request(plan.Id,
            member: new MemberContext { AgeYears = 28, Gender = BenefitMemberGender.Male });

        var result = Gate().PickApplicable(plan, "98", request, request.Lines[0]);

        Assert.Null(result.Selected);
        Assert.Equal(1, result.CandidateCount);
    }
}
