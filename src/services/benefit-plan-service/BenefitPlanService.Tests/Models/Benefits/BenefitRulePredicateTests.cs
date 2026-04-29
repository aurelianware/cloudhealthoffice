using BenefitPlanService.Models.Benefits;
using CloudHealthOffice.BenefitEngine.Domain;

namespace BenefitPlanService.Tests.Models.Benefits;

/// <summary>
/// Contract tests for <see cref="BenefitRulePredicate.Evaluate"/>: each
/// facet is independent and an unset facet is "no opinion". A predicate
/// gates the benefit only when every set facet matches the supplied
/// <see cref="BenefitRuleEvaluationContext"/>.
/// </summary>
public class BenefitRulePredicateTests
{
    [Fact]
    public void Empty_predicate_evaluates_to_true()
    {
        var pred = new BenefitRulePredicate();
        var ctx = new BenefitRuleEvaluationContext();

        pred.Evaluate(ctx).Should().BeTrue();
    }

    [Fact]
    public void Null_context_evaluates_to_false()
    {
        var pred = new BenefitRulePredicate();

        pred.Evaluate(null!).Should().BeFalse();
    }

    [Theory]
    [InlineData(17, false)]
    [InlineData(18, true)]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void Age_range_inclusive_at_both_ends(int age, bool expected)
    {
        var pred = new BenefitRulePredicate { MemberAgeMin = 18, MemberAgeMax = 64 };
        var ctx = new BenefitRuleEvaluationContext { MemberAgeYears = age };

        pred.Evaluate(ctx).Should().Be(expected);
    }

    [Fact]
    public void Age_range_with_missing_age_in_context_fails()
    {
        var pred = new BenefitRulePredicate { MemberAgeMin = 18 };
        var ctx = new BenefitRuleEvaluationContext { MemberAgeYears = null };

        pred.Evaluate(ctx).Should().BeFalse();
    }

    [Fact]
    public void Gender_match_passes_when_equal()
    {
        var pred = new BenefitRulePredicate { MemberGender = BenefitMemberGender.Female };
        var ctx = new BenefitRuleEvaluationContext { MemberGender = BenefitMemberGender.Female };

        pred.Evaluate(ctx).Should().BeTrue();
    }

    [Fact]
    public void Gender_match_fails_when_different()
    {
        var pred = new BenefitRulePredicate { MemberGender = BenefitMemberGender.Female };
        var ctx = new BenefitRuleEvaluationContext { MemberGender = BenefitMemberGender.Male };

        pred.Evaluate(ctx).Should().BeFalse();
    }

    [Fact]
    public void Gender_Any_in_predicate_skips_the_check()
    {
        var pred = new BenefitRulePredicate { MemberGender = BenefitMemberGender.Any };
        var ctx = new BenefitRuleEvaluationContext { MemberGender = BenefitMemberGender.Male };

        pred.Evaluate(ctx).Should().BeTrue();
    }

    [Fact]
    public void Diagnosis_match_uses_case_insensitive_OR_semantics()
    {
        var pred = new BenefitRulePredicate
        {
            RequiredDiagnosisCodes = new List<string> { "E11.9", "E10.9" }
        };
        var ctx = new BenefitRuleEvaluationContext
        {
            DiagnosisCodes = new[] { "I10", "e11.9" } // any-of, case-insensitive
        };

        pred.Evaluate(ctx).Should().BeTrue();
    }

    [Fact]
    public void Diagnosis_match_fails_when_none_present()
    {
        var pred = new BenefitRulePredicate
        {
            RequiredDiagnosisCodes = new List<string> { "E11.9" }
        };
        var ctx = new BenefitRuleEvaluationContext
        {
            DiagnosisCodes = new[] { "I10" }
        };

        pred.Evaluate(ctx).Should().BeFalse();
    }

    [Fact]
    public void Empty_required_diagnosis_list_is_no_opinion()
    {
        var pred = new BenefitRulePredicate
        {
            RequiredDiagnosisCodes = new List<string>()
        };
        var ctx = new BenefitRuleEvaluationContext { DiagnosisCodes = Array.Empty<string>() };

        pred.Evaluate(ctx).Should().BeTrue();
    }

    [Fact]
    public void Related_encounter_lookback_uses_supplied_predicate()
    {
        var seenLookback = -1;
        var pred = new BenefitRulePredicate
        {
            RequiresRelatedEncounter = true,
            RelatedEncounterLookbackDays = 90,
        };
        var ctx = new BenefitRuleEvaluationContext
        {
            HasRelatedEncounter = days =>
            {
                seenLookback = days;
                return true;
            }
        };

        pred.Evaluate(ctx).Should().BeTrue();
        seenLookback.Should().Be(90);
    }

    [Fact]
    public void Related_encounter_required_but_predicate_returns_false_fails()
    {
        var pred = new BenefitRulePredicate { RequiresRelatedEncounter = true };
        var ctx = new BenefitRuleEvaluationContext { HasRelatedEncounter = _ => false };

        pred.Evaluate(ctx).Should().BeFalse();
    }

    [Fact]
    public void Related_encounter_required_but_no_source_supplied_fails_closed()
    {
        var pred = new BenefitRulePredicate { RequiresRelatedEncounter = true };
        var ctx = new BenefitRuleEvaluationContext { HasRelatedEncounter = null };

        pred.Evaluate(ctx).Should().BeFalse(
            "the predicate is conservative — without a related-encounter source it refuses to gate the benefit through");
    }

    [Fact]
    public void All_facets_must_match_for_predicate_to_pass()
    {
        var pred = new BenefitRulePredicate
        {
            MemberAgeMin = 50,
            MemberGender = BenefitMemberGender.Female,
            RequiredDiagnosisCodes = new List<string> { "Z12.31" }, // mammogram screening
        };

        var passing = new BenefitRuleEvaluationContext
        {
            MemberAgeYears = 55,
            MemberGender = BenefitMemberGender.Female,
            DiagnosisCodes = new[] { "Z12.31" }
        };
        var wrongAge = new BenefitRuleEvaluationContext
        {
            MemberAgeYears = 40,
            MemberGender = BenefitMemberGender.Female,
            DiagnosisCodes = new[] { "Z12.31" }
        };

        pred.Evaluate(passing).Should().BeTrue();
        pred.Evaluate(wrongAge).Should().BeFalse();
    }
}
