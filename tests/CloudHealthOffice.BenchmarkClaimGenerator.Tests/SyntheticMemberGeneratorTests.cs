using CloudHealthOffice.BenchmarkClaimGenerator.Configuration;
using CloudHealthOffice.BenchmarkClaimGenerator.Generators;
using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Tests;

public class SyntheticMemberGeneratorTests
{
    private readonly SyntheticMemberGenerator _generator;
    private readonly MemberPoolProfile _smallProfile;
    private readonly List<SyntheticBenefitPlan> _plans;

    public SyntheticMemberGeneratorTests()
    {
        _generator = new SyntheticMemberGenerator();
        _smallProfile = new MemberPoolProfile
        {
            SubscriberCount = 100,
            Seed = 42,
            TenantId = "test-tenant",
        };
        _plans = SyntheticBenefitPlanGenerator.Generate(42);
    }

    [Fact]
    public void Generate_ProducesCorrectSubscriberCount()
    {
        var members = _generator.Generate(_smallProfile, _plans);
        Assert.Equal(100, members.Count);
    }

    [Fact]
    public void Generate_SubscribersHaveValidMemberIds()
    {
        var members = _generator.Generate(_smallProfile, _plans);

        foreach (var m in members)
        {
            Assert.StartsWith("MCC-MBR-", m.MemberId);
            Assert.StartsWith("MCC-SUB-", m.SubscriberId);
        }
    }

    [Fact]
    public void Generate_SubscribersAreMarkedAsSubscribers()
    {
        var members = _generator.Generate(_smallProfile, _plans);

        foreach (var m in members)
        {
            Assert.True(m.IsSubscriber);
            Assert.Equal("Self", m.Relationship);
            Assert.Equal("18", m.RelationshipCode);
        }
    }

    [Fact]
    public void Generate_AllSubscribersHaveTexasAddress()
    {
        var members = _generator.Generate(_smallProfile, _plans);

        foreach (var m in members)
        {
            Assert.Equal("TX", m.State);
            Assert.NotEmpty(m.City);
            Assert.NotEmpty(m.ZipCode);
            Assert.NotEmpty(m.Address);
        }
    }

    [Fact]
    public void Generate_AllMembersHaveAtLeastOneCoverageRecord()
    {
        var members = _generator.Generate(_smallProfile, _plans);

        foreach (var m in members)
        {
            Assert.NotEmpty(m.Coverages);
            Assert.All(m.Coverages, c =>
            {
                Assert.NotEmpty(c.MemberId);
                Assert.NotEmpty(c.PlanId);
                Assert.NotEmpty(c.InsuranceLineCode);
                Assert.True(c.EffectiveDate > DateTime.MinValue);
            });
        }
    }

    [Fact]
    public void Generate_CoverageEffectiveDatesPrecedeToday()
    {
        var members = _generator.Generate(_smallProfile, _plans);

        foreach (var m in members)
        {
            foreach (var cov in m.Coverages)
            {
                Assert.True(cov.EffectiveDate <= DateTime.Today,
                    $"Coverage {cov.Id} has future effective date {cov.EffectiveDate}");
            }
        }
    }

    [Fact]
    public void Generate_ProducesApproximatelyCorrectDependentCount()
    {
        var profile = new MemberPoolProfile
        {
            SubscriberCount = 1000,
            Seed = 42,
            TenantId = "test-tenant",
        };
        var members = _generator.Generate(profile, _plans);

        var totalDependents = members.Sum(m => m.Dependents.Count);
        var totalMembers = members.Count + totalDependents;

        // Should be roughly 1.5 dependents per subscriber on average
        // With 1000 subscribers, expect ~1500 total members (±300 for random variation)
        Assert.InRange(totalMembers, 1100, 2000);
    }

    [Fact]
    public void Generate_DependentsHaveCorrectRelationships()
    {
        var members = _generator.Generate(_smallProfile, _plans);

        foreach (var m in members)
        {
            foreach (var dep in m.Dependents)
            {
                Assert.Contains(dep.RelationshipCode, new[] { "01", "19" });
                Assert.Contains(dep.Relationship, new[] { "Spouse", "Child" });
                Assert.Equal(m.SubscriberId, dep.SubscriberId);
                Assert.Equal(m.MemberId, dep.SubscriberMemberId);
                Assert.Equal(m.LastName, dep.LastName);
            }
        }
    }

    [Fact]
    public void Generate_TerminatedMembersExist()
    {
        var profile = new MemberPoolProfile
        {
            SubscriberCount = 200,
            Seed = 42,
            TenantId = "test-tenant",
            ActiveRate = 0.90,
        };
        var members = _generator.Generate(profile, _plans);

        var terminated = members.Count(m => m.EnrollmentStatus == "Terminated");
        // Expect about 10% terminated (±5% variance)
        Assert.InRange(terminated, 5, 50);
    }

    [Fact]
    public void Generate_DeterministicWithSameSeed()
    {
        var members1 = _generator.Generate(_smallProfile, _plans);
        var members2 = _generator.Generate(_smallProfile, _plans);

        Assert.Equal(members1.Count, members2.Count);
        for (int i = 0; i < members1.Count; i++)
        {
            Assert.Equal(members1[i].MemberId, members2[i].MemberId);
            Assert.Equal(members1[i].FirstName, members2[i].FirstName);
            Assert.Equal(members1[i].LastName, members2[i].LastName);
            Assert.Equal(members1[i].DateOfBirth, members2[i].DateOfBirth);
            Assert.Equal(members1[i].PlanId, members2[i].PlanId);
        }
    }

    [Fact]
    public void Generate_AssignsValidPlanIds()
    {
        var members = _generator.Generate(_smallProfile, _plans);
        var validPlanIds = _plans.Select(p => p.PlanId).ToHashSet();

        foreach (var m in members)
        {
            Assert.Contains(m.PlanId, validPlanIds);
        }
    }

    [Fact]
    public void Generate_CoverageLevelMatchesFamilyComposition()
    {
        var members = _generator.Generate(_smallProfile, _plans);

        foreach (var m in members)
        {
            if (m.Dependents.Count == 0)
            {
                Assert.All(m.Coverages, c => Assert.Equal("EMP", c.CoverageLevelCode));
            }
            else
            {
                // With dependents, should be ESP, ECH, or FAM
                Assert.All(m.Coverages, c =>
                    Assert.Contains(c.CoverageLevelCode, new[] { "ESP", "ECH", "FAM" }));
            }
        }
    }

    [Fact]
    public void GenerateDateOfBirth_ProducesCorrectAgeDistribution()
    {
        var random = new Random(42);
        var dist = new AgeDistribution();
        var ages = new List<int>();

        for (int i = 0; i < 1000; i++)
        {
            var dob = SyntheticMemberGenerator.GenerateDateOfBirth(random, dist);
            var age = (DateTime.Today - dob).Days / 365;
            ages.Add(age);
        }

        var under18 = ages.Count(a => a < 18);
        var age18to44 = ages.Count(a => a >= 18 && a <= 44);
        var age45to64 = ages.Count(a => a >= 45 && a <= 64);
        var age65plus = ages.Count(a => a >= 65);

        // Allow ±10% variance from expected distribution
        Assert.InRange(under18, 150, 350);   // expected ~250
        Assert.InRange(age18to44, 200, 500); // expected ~350
        Assert.InRange(age45to64, 150, 450); // expected ~300
        Assert.InRange(age65plus, 30, 200);  // expected ~100
    }
}
