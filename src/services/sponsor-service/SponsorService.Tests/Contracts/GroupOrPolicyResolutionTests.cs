using Microsoft.Extensions.Logging.Abstractions;
using SponsorService.Contracts;
using SponsorService.Models;
using SponsorService.Tests.Fakes;

namespace SponsorService.Tests.Contracts;

/// <summary>
/// Resolving an inbound identifier that may be a group number or a policy
/// number — the 834's <c>REF*1L</c>
/// (docs/architecture/plan-configuration.md §9b, step 3d).
///
/// <para>
/// The property that matters most here is that it is <em>additive</em>: the
/// group number is tried first and wins outright, so every trading partner
/// sending one today is read exactly as it is read now, and a policy number is
/// consulted only for a value that means nothing at all today. Any change to
/// that order is a change to how existing files are interpreted, which is why
/// it has a test of its own rather than being left to the reading of the code.
/// </para>
/// </summary>
public class GroupOrPolicyResolutionTests
{
    private const string Tenant = "t1";

    private readonly InMemoryContractStore _store = new();
    private readonly InMemorySponsorRepository _sponsors = new();
    private readonly FixedClock _clock = new(new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc));

    private ContractService Service => new(_store, _sponsors, _clock, NullLogger<ContractService>.Instance);
    private static TestActor Manager() => new("am", Tenant, "config:sponsor");

    private void Sponsor(string groupNumber, string name = "Acme Manufacturing") =>
        _sponsors.Put(new Sponsor
        {
            TenantId = Tenant, Id = "s-" + groupNumber, GroupNumber = groupNumber, EmployerName = name,
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

    private async Task PolicyAsync(string groupNumber, string policyNumber, string planId = "PL1", DateTime? to = null)
    {
        var result = await Service.CreatePolicyAsync(Manager(), groupNumber, new PolicyWrite
        {
            PolicyNumber = policyNumber,
            Name = "Policy " + policyNumber,
            BenefitPlanId = planId,
            EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EffectiveTo = to,
        });
        result.Outcome.Should().Be(ContractOutcome.Ok);
    }

    [Fact]
    public async Task A_group_number_resolves_as_a_group_number()
    {
        Sponsor("123456");

        var resolved = await Service.ResolveGroupOrPolicyAsync(Tenant, "123456");

        resolved.Outcome.Should().Be(GroupOrPolicyOutcome.GroupNumber);
        resolved.GroupNumber.Should().Be("123456");
        resolved.Policy.Should().BeNull();
    }

    [Fact]
    public async Task A_policy_number_resolves_to_its_group_and_its_policy()
    {
        Sponsor("123456");
        await PolicyAsync("123456", "123456-0001", planId: "PPO500");

        var resolved = await Service.ResolveGroupOrPolicyAsync(Tenant, "123456-0001");

        resolved.Outcome.Should().Be(GroupOrPolicyOutcome.PolicyNumber);
        resolved.GroupNumber.Should().Be("123456");
        resolved.Policy!.PolicyNumber.Should().Be("123456-0001");
        resolved.Policy.BenefitPlanId.Should().Be("PPO500");
    }

    [Fact]
    public async Task A_group_number_wins_over_a_policy_number_that_happens_to_match_it()
    {
        // The additive promise in one test. If a group is called "SHARED" and
        // some other group has a policy numbered "SHARED", the file has always
        // meant the group and still does. Reversing this order would silently
        // re-file an existing partner's members under a different employer.
        Sponsor("SHARED");
        Sponsor("999999", "Other Employer");
        await PolicyAsync("999999", "SHARED");

        var resolved = await Service.ResolveGroupOrPolicyAsync(Tenant, "SHARED");

        resolved.Outcome.Should().Be(GroupOrPolicyOutcome.GroupNumber);
        resolved.GroupNumber.Should().Be("SHARED");
    }

    [Fact]
    public async Task One_policy_number_on_two_groups_is_ambiguous_rather_than_the_first_one_found()
    {
        // Nothing makes a policy number unique across groups, so this is a
        // real state rather than a corrupt one. Picking either would file a
        // member under the wrong employer, which is worse than refusing.
        Sponsor("111111", "First Employer");
        Sponsor("222222", "Second Employer");
        await PolicyAsync("111111", "STANDARD-01");
        await PolicyAsync("222222", "STANDARD-01");

        var resolved = await Service.ResolveGroupOrPolicyAsync(Tenant, "STANDARD-01");

        resolved.Outcome.Should().Be(GroupOrPolicyOutcome.Ambiguous);
        resolved.GroupNumber.Should().BeNull();
        resolved.CandidateGroupNumbers.Should().Equal("111111", "222222");
    }

    [Fact]
    public async Task Two_policies_of_one_group_cannot_be_ambiguous()
    {
        // A group cannot hold the same policy number twice, so several matches
        // always means several groups. This pins that the count is of groups
        // and not of policies.
        Sponsor("123456");
        await PolicyAsync("123456", "123456-0001");
        await PolicyAsync("123456", "123456-0002");

        var resolved = await Service.ResolveGroupOrPolicyAsync(Tenant, "123456-0002");

        resolved.Outcome.Should().Be(GroupOrPolicyOutcome.PolicyNumber);
        resolved.GroupNumber.Should().Be("123456");
    }

    [Fact]
    public async Task A_policy_whose_offering_has_ended_still_identifies_its_group()
    {
        // A file naming a policy that ended is a file about the right group.
        // The date is the enrollment's problem to report; failing to identify
        // the sponsor at all would turn a reportable date into an unmatched
        // record.
        Sponsor("123456");
        await PolicyAsync("123456", "123456-0001", to: new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc));

        var resolved = await Service.ResolveGroupOrPolicyAsync(Tenant, "123456-0001");

        resolved.Outcome.Should().Be(GroupOrPolicyOutcome.PolicyNumber);
        resolved.GroupNumber.Should().Be("123456");
    }

    [Fact]
    public async Task A_value_that_is_neither_is_unknown()
    {
        Sponsor("123456");
        await PolicyAsync("123456", "123456-0001");

        var resolved = await Service.ResolveGroupOrPolicyAsync(Tenant, "NOT-A-THING");

        resolved.Outcome.Should().Be(GroupOrPolicyOutcome.Unknown);
        resolved.GroupNumber.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_value_is_unknown_rather_than_matching_everything(string value)
    {
        Sponsor("123456");

        (await Service.ResolveGroupOrPolicyAsync(Tenant, value)).Outcome
            .Should().Be(GroupOrPolicyOutcome.Unknown);
    }

    [Fact]
    public async Task Surrounding_space_is_trimmed_because_an_X12_element_often_carries_it()
    {
        Sponsor("123456");
        await PolicyAsync("123456", "123456-0001");

        (await Service.ResolveGroupOrPolicyAsync(Tenant, "  123456  ")).Outcome
            .Should().Be(GroupOrPolicyOutcome.GroupNumber);
        (await Service.ResolveGroupOrPolicyAsync(Tenant, " 123456-0001 ")).Outcome
            .Should().Be(GroupOrPolicyOutcome.PolicyNumber);
    }

    [Fact]
    public async Task Another_tenants_group_and_policy_are_not_visible()
    {
        Sponsor("123456");
        await PolicyAsync("123456", "123456-0001");

        var resolved = await Service.ResolveGroupOrPolicyAsync("other-tenant", "123456-0001");

        resolved.Outcome.Should().Be(GroupOrPolicyOutcome.Unknown);
        (await Service.ResolveGroupOrPolicyAsync("other-tenant", "123456")).Outcome
            .Should().Be(GroupOrPolicyOutcome.Unknown);
    }
}
