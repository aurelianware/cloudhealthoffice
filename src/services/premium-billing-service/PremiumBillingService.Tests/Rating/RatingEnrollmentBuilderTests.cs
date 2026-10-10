using PremiumBillingService.Clients;
using PremiumBillingService.Rating;
using static PremiumBillingService.Tests.Rating.RatingFixtures;

namespace PremiumBillingService.Tests.Rating;

/// <summary>coverage-service records (one per member) + member-service census → rated households. Synthetic data.</summary>
public class RatingEnrollmentBuilderTests
{
    private static readonly DateTime WindowStart = D(2025, 12, 1);
    private static readonly DateTime WindowEnd = D(2026, 3, 31);

    internal static CoverageDto Coverage(string id, string memberId, DateTime effective, DateTime? term = null, string plan = "PPO-GOLD") => new()
    {
        CoverageId = id,
        MemberId = memberId,
        GroupNumber = "G1",
        PlanId = plan,
        InsuranceLineCode = "HLT",
        EffectiveDate = effective,
        TerminationDate = term
    };

    internal static RatingCensusMemberDto Person(string id, string? subscriberId, string? rel, DateTime? dob, bool? tobacco = null) => new()
    {
        MemberId = id,
        IsSubscriber = subscriberId == null,
        SubscriberMemberId = subscriberId,
        RelationshipCode = rel,
        FirstName = "Test",
        LastName = id,
        DateOfBirth = dob,
        TobaccoUser = tobacco
    };

    [Fact]
    public void DependentsJoinTheirSubscribersCoverage_WithTheirOwnDates()
    {
        var build = RatingEnrollmentBuilder.Build(
            new[]
            {
                Coverage("cov-S", "S", D(2026, 1, 1)),
                Coverage("cov-SP", "SP", D(2026, 1, 1)),
                Coverage("cov-K", "K", D(2026, 3, 10)),
            },
            new[]
            {
                Person("S", null, "18", D(1980, 6, 15), tobacco: true),
                Person("SP", "S", "01", D(1982, 3, 1)),
                Person("K", "S", "19", D(2015, 1, 1)),
            },
            WindowStart, WindowEnd);

        build.Issues.Should().BeEmpty();
        var household = build.Enrollments.Should().ContainSingle().Subject;
        household.CoverageId.Should().Be("cov-S");
        household.Members.Select(m => (m.MemberId, m.Relationship, m.EffectiveDate)).Should().Equal(
            ("S", MemberRelationship.Subscriber, (DateTime?)null),
            ("SP", MemberRelationship.Spouse, (DateTime?)D(2026, 1, 1)),
            ("K", MemberRelationship.Child, (DateTime?)D(2026, 3, 10)));
        household.Members[0].TobaccoUser.Should().BeTrue();
        household.Members[0].MemberName.Should().Be("Test S");
    }

    [Theory]
    [InlineData("01", MemberRelationship.Spouse)]
    [InlineData("53", MemberRelationship.Spouse)]
    [InlineData("19", MemberRelationship.Child)]
    [InlineData("17", MemberRelationship.Child)]
    [InlineData("38", MemberRelationship.OtherDependent)]
    public void RelationshipCodes(string code, MemberRelationship expected)
    {
        RatingEnrollmentBuilder.Relationship(Person("D", "S", code, D(2000, 1, 1)), out _).Should().Be(expected);
    }

    [Fact]
    public void CancelledAndOutOfWindowCoverage_IsIgnored()
    {
        var build = RatingEnrollmentBuilder.Build(
            new[]
            {
                Coverage("cov-cancelled", "A", D(2026, 2, 1), D(2026, 1, 31)),
                Coverage("cov-old", "B", D(2024, 1, 1), D(2025, 6, 30)),
                Coverage("cov-future", "C", D(2026, 5, 1)),
            },
            new[] { Person("A", null, "18", D(1980, 1, 1)), Person("B", null, "18", D(1980, 1, 1)), Person("C", null, "18", D(1980, 1, 1)) },
            WindowStart, WindowEnd);

        build.Enrollments.Should().BeEmpty();
        build.Issues.Should().BeEmpty();
    }

    [Fact]
    public void MissingDateOfBirth_ExcludesTheHousehold_AndIsReported()
    {
        var build = RatingEnrollmentBuilder.Build(
            new[] { Coverage("cov-S", "S", D(2026, 1, 1)), Coverage("cov-SP", "SP", D(2026, 1, 1)) },
            new[] { Person("S", null, "18", D(1980, 6, 15)), Person("SP", "S", "01", null) },
            WindowStart, WindowEnd);

        build.Enrollments.Should().BeEmpty("a household missing a member would bill the wrong tier");
        build.ExcludedCoverageIds.Should().Contain("cov-S");
        build.Issues.Should().ContainSingle(i => i.MemberId == "SP" && i.Message.Contains("date of birth"));
    }

    [Fact]
    public void UnknownMember_OrphanDependent_AndMissingRelationship_AreReported()
    {
        var build = RatingEnrollmentBuilder.Build(
            new[]
            {
                Coverage("cov-ghost", "GHOST", D(2026, 1, 1)),
                Coverage("cov-orphan", "O", D(2026, 1, 1)),
                Coverage("cov-T", "T", D(2026, 1, 1)),
                Coverage("cov-norel", "NR", D(2026, 1, 1)),
            },
            new[]
            {
                Person("O", "NOBODY", "19", D(2015, 1, 1)),
                Person("T", null, "18", D(1980, 1, 1)),
                Person("NR", "T", null, D(2015, 1, 1)),
            },
            WindowStart, WindowEnd);

        build.Issues.Select(i => i.CoverageId).Should().BeEquivalentTo("cov-ghost", "cov-orphan", "cov-norel");
        build.ExcludedCoverageIds.Should().Contain("cov-T");
        build.Enrollments.Should().BeEmpty();
    }

    [Fact]
    public void OverlappingSubscriberCoverage_IsReported_NotBilledTwice()
    {
        var build = RatingEnrollmentBuilder.Build(
            new[] { Coverage("cov-1", "S", D(2026, 1, 1)), Coverage("cov-2", "S", D(2026, 2, 1)) },
            new[] { Person("S", null, "18", D(1980, 1, 1)) },
            WindowStart, WindowEnd);

        build.Enrollments.Should().BeEmpty();
        build.Issues.Should().ContainSingle();
        build.ExcludedCoverageIds.Should().BeEquivalentTo("cov-1", "cov-2");
    }

    [Fact]
    public void ReEnrollmentAfterAGap_IsTwoHouseholds()
    {
        var build = RatingEnrollmentBuilder.Build(
            new[] { Coverage("cov-1", "S", D(2025, 12, 1), D(2026, 1, 15)), Coverage("cov-2", "S", D(2026, 3, 1)) },
            new[] { Person("S", null, "18", D(1980, 1, 1)) },
            WindowStart, WindowEnd);

        build.Issues.Should().BeEmpty();
        build.Enrollments.Select(e => e.CoverageId).Should().Equal("cov-1", "cov-2");
    }
}
