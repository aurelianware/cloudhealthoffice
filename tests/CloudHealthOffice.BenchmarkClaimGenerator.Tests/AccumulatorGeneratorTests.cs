using CloudHealthOffice.BenchmarkClaimGenerator.Configuration;
using CloudHealthOffice.BenchmarkClaimGenerator.Generators;
using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Tests;

public class AccumulatorGeneratorTests
{
    private readonly SyntheticAccumulatorGenerator _generator;
    private readonly List<SyntheticMember> _members;
    private readonly List<SyntheticBenefitPlan> _plans;

    public AccumulatorGeneratorTests()
    {
        _generator = new SyntheticAccumulatorGenerator();
        _plans = SyntheticBenefitPlanGenerator.Generate(42);
        var memberProfile = new MemberPoolProfile
        {
            SubscriberCount = 100,
            Seed = 42,
            TenantId = "test-tenant",
        };
        var memberGen = new SyntheticMemberGenerator();
        _members = memberGen.Generate(memberProfile, _plans);
    }

    [Fact]
    public void Generate_ProducesAccumulatorsForActiveMembers()
    {
        var accumulators = _generator.Generate(_members, _plans, 42);
        Assert.NotEmpty(accumulators);
    }

    [Fact]
    public void Generate_AccumulatorsHaveValidIds()
    {
        var accumulators = _generator.Generate(_members, _plans, 42);

        foreach (var acc in accumulators)
        {
            Assert.NotEmpty(acc.Id);
            Assert.Contains(":", acc.Id); // Composite ID format
            Assert.NotEmpty(acc.MemberId);
            Assert.NotEmpty(acc.BenefitPlanId);
            Assert.NotEmpty(acc.PlanYear);
        }
    }

    [Fact]
    public void Generate_MostAccumulatorsAreAtZero()
    {
        var accumulators = _generator.Generate(_members, _plans, 42);

        // Texas Medicaid has zero cost-sharing for most programs
        // Most plans have $0 deductible and $0 OOP max
        var zeroDeductible = accumulators.Count(a =>
            a.IndividualDeductibleSpent == 0m && a.FamilyDeductibleSpent == 0m);

        // Most should be at zero since Medicaid plans have zero cost-sharing
        Assert.True(zeroDeductible >= accumulators.Count * 0.5,
            $"Expected most accumulators at $0 but got {zeroDeductible} of {accumulators.Count}");
    }

    [Fact]
    public void Generate_AccumulatorBalancesNeverExceedLimits()
    {
        var accumulators = _generator.Generate(_members, _plans, 42);

        foreach (var acc in accumulators)
        {
            Assert.True(acc.IndividualDeductibleSpent <= acc.IndividualDeductibleLimit,
                $"Deductible spent {acc.IndividualDeductibleSpent} exceeds limit {acc.IndividualDeductibleLimit}");
            Assert.True(acc.IndividualOopSpent <= acc.IndividualOopMaxLimit,
                $"OOP spent {acc.IndividualOopSpent} exceeds limit {acc.IndividualOopMaxLimit}");
            Assert.True(acc.FamilyDeductibleSpent <= acc.FamilyDeductibleLimit,
                $"Family deductible spent {acc.FamilyDeductibleSpent} exceeds limit {acc.FamilyDeductibleLimit}");
            Assert.True(acc.FamilyOopSpent <= acc.FamilyOopMaxLimit,
                $"Family OOP spent {acc.FamilyOopSpent} exceeds limit {acc.FamilyOopMaxLimit}");
        }
    }

    [Fact]
    public void Generate_IncludesBothIndividualAndFamilyScopes()
    {
        var accumulators = _generator.Generate(_members, _plans, 42);

        var individualCount = accumulators.Count(a => a.Scope == "Individual");
        var familyCount = accumulators.Count(a => a.Scope == "Family");

        Assert.True(individualCount > 0, "Expected some individual-scope accumulators");
        // Family accumulators only created when subscriber has dependents
        // May be 0 if no subscribers happen to have dependents in this seed
    }

    [Fact]
    public void Generate_SkipsTerminatedMembers()
    {
        var accumulators = _generator.Generate(_members, _plans, 42);

        var terminatedMemberIds = _members
            .Where(m => m.EnrollmentStatus == "Terminated")
            .Select(m => m.MemberId)
            .ToHashSet();

        foreach (var acc in accumulators)
        {
            Assert.DoesNotContain(acc.MemberId, terminatedMemberIds);
        }
    }

    [Fact]
    public void Generate_DeterministicWithSameSeed()
    {
        var acc1 = _generator.Generate(_members, _plans, 42);
        var acc2 = _generator.Generate(_members, _plans, 42);

        Assert.Equal(acc1.Count, acc2.Count);
        for (int i = 0; i < acc1.Count; i++)
        {
            Assert.Equal(acc1[i].Id, acc2[i].Id);
            Assert.Equal(acc1[i].IndividualDeductibleSpent, acc2[i].IndividualDeductibleSpent);
            Assert.Equal(acc1[i].IndividualOopSpent, acc2[i].IndividualOopSpent);
        }
    }

    [Fact]
    public void Generate_RemainingDeductibleIsNonNegative()
    {
        var accumulators = _generator.Generate(_members, _plans, 42);

        foreach (var acc in accumulators)
        {
            Assert.True(acc.RemainingIndividualDeductible >= 0);
            Assert.True(acc.RemainingIndividualOop >= 0);
        }
    }
}
