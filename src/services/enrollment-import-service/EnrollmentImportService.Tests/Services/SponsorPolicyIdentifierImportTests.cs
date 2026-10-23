using EnrollmentImportService.Clients;
using EnrollmentImportService.Models;

namespace EnrollmentImportService.Tests.Services;

/// <summary>
/// An 834's REF*1L may be the sponsor's group number or one of that sponsor's
/// policy numbers (docs/architecture/plan-configuration.md §9b, §12.12).
///
/// <para>
/// The property these exist to hold still is that the change is
/// <em>additive</em>. A group number is tried first and wins outright, so every
/// partner sending one today is read exactly as it is read now, and a value
/// that is still neither number is left as the file sent it — which is what
/// keeps the long-standing "create the employer from the N1 loop" path intact.
/// Only a value that means nothing today, and would otherwise have
/// manufactured an employer named after a policy, is read differently.
/// </para>
/// </summary>
public partial class EnrollmentImportServiceDependentTests
{
    private static SponsorIdentifier Group(string groupNumber) =>
        new(SponsorIdentifierKind.GroupNumber, groupNumber);

    private static SponsorIdentifier Policy(string groupNumber, string policyNumber) =>
        new(SponsorIdentifierKind.PolicyNumber, groupNumber, policyNumber);

    private static MemberEnrollment Insured(string id, string groupNumber)
    {
        var enrollment = Subscriber(id, "021");
        enrollment.GroupNumber = groupNumber;
        enrollment.Coverage.Add(new CoverageDetail { InsuranceLineCode = "HLT", PlanCoverageDescription = "PPO2026" });
        return enrollment;
    }

    [Fact]
    public async Task A_group_number_is_filed_exactly_as_it_always_was()
    {
        var h = new Harness { ResolveIdentifier = _ => Group("GRP0001") };

        var result = await h.ImportAsync(new Enrollment834
        {
            BatchId = "B-group",
            Enrollments = { Insured("SUB1", "GRP0001") },
        });

        result.FailedCount.Should().Be(0);
        h.Created.Single(c => c.IsSubscriber).GroupNumber.Should().Be("GRP0001");
        var coverage = h.Coverage.Should().ContainSingle().Subject;
        coverage.GroupNumber.Should().Be("GRP0001");
        coverage.SponsorPolicyNumber.Should().BeNull("the file named the group, so there is no policy to record");
    }

    [Fact]
    public async Task A_policy_number_files_the_member_under_its_group_and_records_the_policy()
    {
        // The whole point: "GRP0001-0002" is a policy of GRP0001, so the member
        // belongs to GRP0001 and the policy is the extra fact worth keeping.
        var h = new Harness { ResolveIdentifier = _ => Policy("GRP0001", "GRP0001-0002") };

        var result = await h.ImportAsync(new Enrollment834
        {
            BatchId = "B-policy",
            Enrollments = { Insured("SUB1", "GRP0001-0002") },
        });

        result.FailedCount.Should().Be(0);
        h.Created.Single(c => c.IsSubscriber).GroupNumber
            .Should().Be("GRP0001", "the member belongs to the employer, not to the policy number");
        var coverage = h.Coverage.Should().ContainSingle().Subject;
        coverage.GroupNumber.Should().Be("GRP0001");
        coverage.SponsorPolicyNumber.Should().Be("GRP0001-0002");
    }

    [Fact]
    public async Task A_policy_number_no_longer_manufactures_an_employer_named_after_it()
    {
        // The defect this closes. Before, an unrecognised REF*1L fell straight
        // through to the create path, so a partner sending a policy number got
        // a brand new employer whose group number was that policy number —
        // silently, and alongside the real employer.
        var h = new Harness { ResolveIdentifier = _ => Policy("GRP0001", "GRP0001-0002") };
        var enrollment = Insured("SUB1", "GRP0001-0002");
        enrollment.Sponsor = new Sponsor { Id = "94-1234567", IdQualifier = "FI", Name = "ACME MANUFACTURING" };

        await h.ImportAsync(new Enrollment834
        {
            BatchId = "B-no-ghost",
            Enrollments = { enrollment },
        });

        h.SponsorsCreated.Should().NotContain("GRP0001-0002");
    }

    [Fact]
    public async Task A_value_that_is_neither_number_still_creates_the_employer_from_the_N1_loop()
    {
        // Unchanged behaviour, and the reason the change is safe to deploy: a
        // genuinely new employer's first file still lands the way it always has.
        var h = new Harness();
        var enrollment = Insured("SUB1", "GRP-NEW");
        enrollment.Sponsor = new Sponsor { Id = "94-7654321", IdQualifier = "FI", Name = "NEW EMPLOYER" };

        var result = await h.ImportAsync(new Enrollment834
        {
            BatchId = "B-new",
            Enrollments = { enrollment },
        });

        result.FailedCount.Should().Be(0);
        h.SponsorsCreated.Should().Contain("GRP-NEW");
        h.Coverage.Should().ContainSingle().Which.GroupNumber.Should().Be("GRP-NEW");
    }

    [Fact]
    public async Task A_policy_number_held_by_two_groups_is_rejected_rather_than_assigned_to_one()
    {
        // Nothing makes a policy number unique across groups, so this is
        // reachable state. Either choice would put a member, their coverage and
        // their premium under the wrong employer with nothing showing it.
        var h = new Harness
        {
            ResolveIdentifier = _ => new SponsorIdentifier(
                SponsorIdentifierKind.Ambiguous, CandidateGroupNumbers: ["GRP0001", "GRP0002"]),
        };

        var result = await h.ImportAsync(new Enrollment834
        {
            BatchId = "B-ambiguous",
            Enrollments = { Insured("SUB1", "STANDARD-01") },
        });

        result.FailedCount.Should().Be(1);
        h.Created.Should().BeEmpty("nothing may be filed when the file does not say which employer it is about");
        h.Coverage.Should().BeEmpty();

        var txn = h.Transactions.Should().ContainSingle().Subject;
        txn.Status.Should().Be("Rejected");
        txn.ReasonCode.Should().Be(EnrollmentRejectionReasons.AmbiguousSponsorIdentifier);
        // Both candidates are named, so whoever works the queue can see the
        // choice rather than having to go and find it.
        txn.Errors.Should().ContainSingle().Which.Message
            .Should().Contain("GRP0001").And.Contain("GRP0002");
    }

    [Fact]
    public async Task One_members_policy_does_not_carry_onto_the_next_member_in_the_same_file()
    {
        // The per-member state that makes this work is held on the batch
        // context next to the file position, so a member on a policy followed
        // by a member on a plain group number is the case that would quietly
        // mis-file the second one.
        var h = new Harness
        {
            ResolveIdentifier = value => value == "GRP0001-0002"
                ? Policy("GRP0001", "GRP0001-0002")
                : Group("GRP0009"),
        };

        await h.ImportAsync(new Enrollment834
        {
            BatchId = "B-mixed",
            Enrollments =
            {
                Insured("SUB1", "GRP0001-0002"),
                Insured("SUB2", "GRP0009"),
            },
        });

        h.Coverage.Should().HaveCount(2);
        var onPolicy = h.Coverage.Single(c => c.MemberId == "SUB1");
        onPolicy.GroupNumber.Should().Be("GRP0001");
        onPolicy.SponsorPolicyNumber.Should().Be("GRP0001-0002");

        var onGroup = h.Coverage.Single(c => c.MemberId == "SUB2");
        onGroup.GroupNumber.Should().Be("GRP0009");
        onGroup.SponsorPolicyNumber.Should().BeNull();
    }

    [Fact]
    public async Task A_dependent_without_its_own_identifier_is_held_under_its_subscribers_policy()
    {
        var h = new Harness { ResolveIdentifier = _ => Policy("GRP0001", "GRP0001-0002") };
        var child = Child("MICHAEL", "20150610", "021");
        var enrollment = Insured("SUB1", "GRP0001-0002");
        enrollment.Dependents = [child];
        child.Coverage = [new CoverageDetail { InsuranceLineCode = "HLT", PlanCoverageDescription = "PPO2026" }];

        await h.ImportAsync(new Enrollment834
        {
            BatchId = "B-dependent",
            Enrollments = { enrollment },
        });

        h.Coverage.Should().HaveCount(2);
        h.Coverage.Should().OnlyContain(c =>
            c.GroupNumber == "GRP0001" && c.SponsorPolicyNumber == "GRP0001-0002");
    }

    [Fact]
    public async Task A_file_of_many_members_under_one_group_asks_sponsor_service_once()
    {
        // Without this an 834 for a large employer would add one HTTP round
        // trip per member, which is the difference between a change that is
        // deployable and one that is not.
        var h = new Harness { ResolveIdentifier = _ => Group("GRP0001") };

        await h.ImportAsync(new Enrollment834
        {
            BatchId = "B-many",
            Enrollments =
            {
                Insured("SUB1", "GRP0001"),
                Insured("SUB2", "GRP0001"),
                Insured("SUB3", "GRP0001"),
            },
        });

        h.IdentifierLookups.Should().Equal("GRP0001");
    }

    [Fact]
    public async Task Not_having_been_able_to_ask_fails_the_member_instead_of_inventing_an_employer()
    {
        // "Could not look" must never read as "there is none": that is exactly
        // the path that creates an employer, and a sponsor-service outage would
        // otherwise fill the tenant with them.
        var h = new Harness
        {
            ResolveIdentifier = _ => throw new SponsorServiceException(
                "sponsor-service answered 503", System.Net.HttpStatusCode.ServiceUnavailable),
        };
        var enrollment = Insured("SUB1", "GRP0001");
        enrollment.Sponsor = new Sponsor { Id = "94-1234567", IdQualifier = "FI", Name = "ACME MANUFACTURING" };

        var result = await h.ImportAsync(new Enrollment834
        {
            BatchId = "B-down",
            Enrollments = { enrollment },
        });

        result.FailedCount.Should().Be(1);
        h.SponsorsCreated.Should().BeEmpty();
        h.Created.Should().BeEmpty();
        h.Transactions.Should().ContainSingle().Which.ReasonCode
            .Should().Be(EnrollmentRejectionReasons.DownstreamUnavailable, "it is safe to reprocess");
    }
}
