using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SponsorService.Contracts;
using SponsorService.Models;
using SponsorService.Tests.Fakes;

namespace SponsorService.Tests.Contracts;

/// <summary>
/// Sponsor policies and contract terms
/// (docs/architecture/plan-configuration.md §9b; step 3 of the §12 build
/// order). Nothing reads these records yet — this stage exists so that the
/// terms, the class and sponsor-policy setting levels, and every consumer have
/// something to read.
///
/// <para>
/// What is worth testing at this stage is the handful of rules a caller could
/// otherwise write and nothing downstream could recover from: a policy whose
/// plan was swapped under its members, two contract periods covering the same
/// day, a term claiming to be signed with nothing attached, and a term
/// claiming to be active before the contract starts.
/// </para>
/// </summary>
public class SponsorContractTests
{
    private const string Tenant = "t1";
    private const string Group = "123456";

    private readonly InMemoryContractStore _store = new();
    private readonly InMemorySponsorRepository _sponsors = new();
    private readonly FixedClock _clock = new(new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc));

    private ContractService Service => new(_store, _sponsors, _clock, NullLogger<ContractService>.Instance);
    private static TestActor Manager(string tenant = Tenant) => new("am", tenant, "config:sponsor");

    public SponsorContractTests()
    {
        _sponsors.Put(new Sponsor
        {
            TenantId = Tenant, Id = "s1", GroupNumber = Group, EmployerName = "Acme Manufacturing",
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
    }

    private static PolicyWrite Ppo => new()
    {
        PolicyNumber = "123456-0001",
        Name = "PPO 500",
        BenefitPlanId = "PLAN-PPO-500",
        ProgramId = "t1|DEFAULT|LARGEGROUP",
        EffectiveFrom = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static TermWrite PlanYear2027 => new()
    {
        PeriodStart = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        PeriodEnd = new DateTime(2027, 12, 31, 0, 0, 0, DateTimeKind.Utc),
    };

    // ── Policies ──────────────────────────────────────────────────

    [Fact]
    public async Task A_policy_is_a_plan_offered_under_its_own_number()
    {
        var result = await Service.CreatePolicyAsync(Manager(), Group, Ppo);

        result.Outcome.Should().Be(ContractOutcome.Ok, string.Join("; ", result.Errors));
        var policy = result.Value!;
        policy.Id.Should().Be("t1|123456|123456-0001");
        policy.PolicyNumber.Should().Be("123456-0001");
        policy.BenefitPlanId.Should().Be("PLAN-PPO-500");
        policy.Revision.Should().Be(0);
    }

    [Fact]
    public async Task A_group_can_offer_more_than_one_policy_but_not_the_same_number_twice()
    {
        await Service.CreatePolicyAsync(Manager(), Group, Ppo);
        var hmo = await Service.CreatePolicyAsync(Manager(), Group, Ppo with
        {
            PolicyNumber = "123456-0002", Name = "HMO Base", BenefitPlanId = "PLAN-HMO-BASE",
        });
        var again = await Service.CreatePolicyAsync(Manager(), Group, Ppo);

        hmo.Outcome.Should().Be(ContractOutcome.Ok);
        again.Outcome.Should().Be(ContractOutcome.Conflict);
        (await Service.ListPoliciesAsync(Tenant, Group)).Value!.Select(p => p.PolicyNumber)
            .Should().Equal("123456-0001", "123456-0002");
    }

    [Fact]
    public async Task A_policy_needs_a_number_and_a_plan()
    {
        var result = await Service.CreatePolicyAsync(Manager(), Group, new PolicyWrite { Name = "PPO 500" });

        result.Outcome.Should().Be(ContractOutcome.Invalid);
        result.Errors.Should().Contain(e => e.Contains("policy number"));
        result.Errors.Should().Contain(e => e.Contains("benefit plan"));
    }

    [Fact]
    public async Task A_policy_under_a_group_the_tenant_does_not_have_is_not_found()
    {
        (await Service.CreatePolicyAsync(Manager(), "999999", Ppo)).Outcome.Should().Be(ContractOutcome.NotFound);
        (await Service.ListPoliciesAsync(Tenant, "999999")).Outcome.Should().Be(ContractOutcome.NotFound);
    }

    [Fact]
    public async Task One_tenants_policies_are_not_anothers()
    {
        await Service.CreatePolicyAsync(Manager(), Group, Ppo);

        // t2 has no sponsor with this group number, so the group is not theirs
        // to read — not an empty list, which would say the group exists and
        // offers nothing.
        (await Service.ListPoliciesAsync("t2", Group)).Outcome.Should().Be(ContractOutcome.NotFound);
        (await Service.CreatePolicyAsync(Manager("t2"), Group, Ppo)).Outcome.Should().Be(ContractOutcome.NotFound);
    }

    [Fact]
    public async Task A_renewal_changes_a_policys_terms_not_its_identity()
    {
        await Service.CreatePolicyAsync(Manager(), Group, Ppo);

        var renamed = await Service.UpdatePolicyAsync(Manager(), Group, "123456-0001", new PolicyWrite
        {
            Name = "PPO 500 (2028)",
            EffectiveTo = new DateTime(2028, 12, 31, 0, 0, 0, DateTimeKind.Utc),
        });

        renamed.Outcome.Should().Be(ContractOutcome.Ok, string.Join("; ", renamed.Errors));
        renamed.Value!.Name.Should().Be("PPO 500 (2028)");
        renamed.Value.PolicyNumber.Should().Be("123456-0001", "the number is on ID cards and inbound 834s");
        renamed.Value.Revision.Should().Be(1);
    }

    [Theory]
    [InlineData("PolicyNumber")]
    [InlineData("BenefitPlanId")]
    [InlineData("ProgramId")]
    public async Task The_fields_that_identify_a_policy_are_refused_rather_than_ignored(string field)
    {
        await Service.CreatePolicyAsync(Manager(), Group, Ppo);
        var write = field switch
        {
            "PolicyNumber" => new PolicyWrite { PolicyNumber = "123456-0009" },
            "BenefitPlanId" => new PolicyWrite { BenefitPlanId = "PLAN-HMO-BASE" },
            _ => new PolicyWrite { ProgramId = "t1|DEFAULT|SMALLGROUP" },
        };

        var result = await Service.UpdatePolicyAsync(Manager(), Group, "123456-0001", write);

        // Refused, not quietly dropped: editing one of these in place would
        // move every member's coverage without an enrollment, and a caller who
        // got a 200 back would believe it had worked.
        result.Outcome.Should().Be(ContractOutcome.Invalid);
        result.Errors.Should().ContainSingle().Which.Should().Contain("new one");
        (await Service.GetPolicyAsync(Tenant, Group, "123456-0001")).Value!.BenefitPlanId.Should().Be("PLAN-PPO-500");
    }

    [Fact]
    public async Task Restating_a_policys_own_plan_is_not_a_change()
    {
        await Service.CreatePolicyAsync(Manager(), Group, Ppo);

        // A caller that sends the whole record back, unchanged, is not trying
        // to replace the plan.
        var result = await Service.UpdatePolicyAsync(Manager(), Group, "123456-0001", new PolicyWrite
        {
            PolicyNumber = "123456-0001", BenefitPlanId = "PLAN-PPO-500",
            ProgramId = "t1|DEFAULT|LARGEGROUP", Name = "PPO 500",
        });

        result.Outcome.Should().Be(ContractOutcome.Ok, string.Join("; ", result.Errors));
    }

    [Fact]
    public async Task A_concurrent_edit_fails_rather_than_being_undone()
    {
        await Service.CreatePolicyAsync(Manager(), Group, Ppo);

        // Somebody else saves their own edit after this one read the policy and
        // before it writes, so the revision the write is based on is gone.
        _store.BeforeReplacePolicy = () => _store.BumpPolicyRevision(Tenant, "t1|123456|123456-0001");

        var result = await Service.UpdatePolicyAsync(Manager(), Group, "123456-0001", new PolicyWrite { Name = "Renamed" });

        result.Outcome.Should().Be(ContractOutcome.Conflict);

        // And the other writer's edit is still there: nothing was undone.
        var stored = (await Service.GetPolicyAsync(Tenant, Group, "123456-0001")).Value!;
        stored.Name.Should().Be("PPO 500");
        stored.Revision.Should().Be(1);
    }

    // ── Classes ───────────────────────────────────────────────────

    [Fact]
    public async Task Classes_are_canonical_and_each_code_appears_once()
    {
        var result = await Service.CreatePolicyAsync(Manager(), Group, Ppo with
        {
            Classes =
            [
                new SponsorClass { Code = " hourly ", Name = "Hourly employees" },
                new SponsorClass { Code = "salaried", Name = "Salaried employees" },
            ],
        });

        result.Value!.Classes.Select(c => c.Code).Should().Equal("HOURLY", "SALARIED");
        // One spelling of a class as a plan-configuration node, computed in one
        // place, so a setting written at class level and a class read off this
        // record cannot drift apart.
        SponsorClass.ScopeIdFor(result.Value.Id, "HOURLY").Should().Be("t1|123456|123456-0001|HOURLY");
    }

    [Fact]
    public async Task Two_classes_with_one_code_are_refused()
    {
        var result = await Service.CreatePolicyAsync(Manager(), Group, Ppo with
        {
            Classes =
            [
                new SponsorClass { Code = "HOURLY", Name = "Hourly" },
                new SponsorClass { Code = "hourly", Name = "Hourly, again" },
            ],
        });

        // They would give a class-level setting two nodes under one scope id.
        result.Outcome.Should().Be(ContractOutcome.Invalid);
        result.Errors.Should().Contain(e => e.Contains("twice"));
    }

    [Fact]
    public async Task A_class_needs_a_code_and_a_name()
    {
        var result = await Service.CreatePolicyAsync(Manager(), Group, Ppo with
        {
            Classes = [new SponsorClass { Code = "MGMT" }, new SponsorClass { Code = "", Name = "Nameless" }],
        });

        result.Outcome.Should().Be(ContractOutcome.Invalid);
        result.Errors.Should().Contain(e => e.Contains("needs a name"));
        result.Errors.Should().Contain(e => e.Contains("needs a code"));
    }

    // ── Contract terms ────────────────────────────────────────────

    [Fact]
    public async Task A_new_group_or_renewal_opens_a_draft_term()
    {
        var result = await Service.CreateTermAsync(Manager(), Group, PlanYear2027);

        result.Outcome.Should().Be(ContractOutcome.Ok, string.Join("; ", result.Errors));
        result.Value!.Id.Should().Be("t1|123456|20270101");
        result.Value.Status.Should().Be(ContractTermStatus.Draft);
    }

    [Fact]
    public async Task Two_terms_cannot_cover_the_same_day()
    {
        await Service.CreateTermAsync(Manager(), Group, PlanYear2027);

        var overlapping = await Service.CreateTermAsync(Manager(), Group, new TermWrite
        {
            PeriodStart = new DateTime(2027, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            PeriodEnd = new DateTime(2028, 6, 30, 0, 0, 0, DateTimeKind.Utc),
        });

        // "The term in force on this date" is the question every consumer of
        // §9b asks, and two overlapping terms make it unanswerable.
        overlapping.Outcome.Should().Be(ContractOutcome.Conflict);
        overlapping.Errors.Should().ContainSingle().Which.Should().Contain("2027-01-01");
    }

    [Fact]
    public async Task An_ended_term_does_not_block_the_period_it_covered()
    {
        await Service.CreateTermAsync(Manager(), Group, PlanYear2027);
        await Service.SetTermStatusAsync(Manager(), Group, PlanYear2027.PeriodStart!.Value,
            new TermStatusWrite { Status = ContractTermStatus.Ended, Reason = "Entered against the wrong group" });

        var replacement = await Service.CreateTermAsync(Manager(), Group, new TermWrite
        {
            PeriodStart = new DateTime(2027, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            PeriodEnd = new DateTime(2028, 1, 31, 0, 0, 0, DateTimeKind.Utc),
        });

        replacement.Outcome.Should().Be(ContractOutcome.Ok, string.Join("; ", replacement.Errors));
    }

    [Fact]
    public async Task A_term_follows_the_flow_and_records_every_move()
    {
        await Service.CreateTermAsync(Manager(), Group, PlanYear2027);
        var start = PlanYear2027.PeriodStart!.Value;

        await Service.SetTermStatusAsync(Manager(), Group, start,
            new TermStatusWrite { Status = ContractTermStatus.InUnderwriting, Reason = "Participation below 75%" });
        await Service.SetTermStatusAsync(Manager(), Group, start,
            new TermStatusWrite { Status = ContractTermStatus.Draft, Reason = "Sent back for a higher contribution" });
        var signed = await Service.SetTermStatusAsync(Manager(), Group, start,
            new TermStatusWrite { Status = ContractTermStatus.Signed, SignedDocumentId = "doc-1" });

        signed.Outcome.Should().Be(ContractOutcome.Ok, string.Join("; ", signed.Errors));
        signed.Value!.SignedDocumentId.Should().Be("doc-1");
        signed.Value.StatusHistory.Select(h => (h.From, h.To)).Should().Equal(
            (ContractTermStatus.Draft, ContractTermStatus.InUnderwriting),
            (ContractTermStatus.InUnderwriting, ContractTermStatus.Draft),
            (ContractTermStatus.Draft, ContractTermStatus.Signed));
        signed.Value.StatusHistory.Should().OnlyContain(h => h.ChangedBy == "am");
    }

    [Fact]
    public async Task Signing_needs_the_signed_agreement()
    {
        await Service.CreateTermAsync(Manager(), Group, PlanYear2027);

        var result = await Service.SetTermStatusAsync(Manager(), Group, PlanYear2027.PeriodStart!.Value,
            new TermStatusWrite { Status = ContractTermStatus.Signed });

        // A term marked signed with nothing attached is a claim nobody can check.
        result.Outcome.Should().Be(ContractOutcome.Invalid);
        result.Errors.Should().ContainSingle().Which.Should().Contain("signed agreement");
    }

    [Fact]
    public async Task A_term_cannot_be_active_before_the_contract_starts()
    {
        var start = PlanYear2027.PeriodStart!.Value;
        await Service.CreateTermAsync(Manager(), Group, PlanYear2027);
        await Service.SetTermStatusAsync(Manager(), Group, start,
            new TermStatusWrite { Status = ContractTermStatus.Signed, SignedDocumentId = "doc-1" });

        var early = await Service.SetTermStatusAsync(Manager(), Group, start, new TermStatusWrite { Status = ContractTermStatus.Active });
        early.Outcome.Should().Be(ContractOutcome.Invalid);
        early.Errors.Should().ContainSingle().Which.Should().Contain("2027-01-01");

        // §9b: it activates on its start date.
        _clock.Now = new DateTime(2027, 1, 1, 6, 0, 0, DateTimeKind.Utc);
        (await Service.SetTermStatusAsync(Manager(), Group, start, new TermStatusWrite { Status = ContractTermStatus.Active }))
            .Outcome.Should().Be(ContractOutcome.Ok);
    }

    [Fact]
    public async Task An_ended_term_is_final()
    {
        var start = PlanYear2027.PeriodStart!.Value;
        await Service.CreateTermAsync(Manager(), Group, PlanYear2027);
        await Service.SetTermStatusAsync(Manager(), Group, start, new TermStatusWrite { Status = ContractTermStatus.Ended });

        var revived = await Service.SetTermStatusAsync(Manager(), Group, start, new TermStatusWrite { Status = ContractTermStatus.Draft });

        // A term that was over and is wanted again is a new term, with its own dates.
        revived.Outcome.Should().Be(ContractOutcome.Invalid);
        revived.Errors.Should().ContainSingle().Which.Should().Contain("final");
    }

    [Fact]
    public async Task Setting_the_status_a_term_already_has_changes_nothing_and_succeeds()
    {
        var start = PlanYear2027.PeriodStart!.Value;
        await Service.CreateTermAsync(Manager(), Group, PlanYear2027);
        await Service.SetTermStatusAsync(Manager(), Group, start,
            new TermStatusWrite { Status = ContractTermStatus.Signed, SignedDocumentId = "doc-1" });

        // A retried call must not add a second history entry.
        var again = await Service.SetTermStatusAsync(Manager(), Group, start,
            new TermStatusWrite { Status = ContractTermStatus.Signed, SignedDocumentId = "doc-1" });

        again.Outcome.Should().Be(ContractOutcome.Ok);
        again.Value!.StatusHistory.Should().ContainSingle();
    }

    [Fact]
    public async Task A_signature_recorded_in_two_calls_is_not_refused_on_the_second()
    {
        var start = PlanYear2027.PeriodStart!.Value;
        await Service.CreateTermAsync(Manager(), Group, PlanYear2027);
        await Service.SetTermStatusAsync(Manager(), Group, start,
            new TermStatusWrite { Status = ContractTermStatus.Signed, SignedDocumentId = "doc-1" });

        var corrected = await Service.SetTermStatusAsync(Manager(), Group, start,
            new TermStatusWrite { Status = ContractTermStatus.Signed, SignedDocumentId = "doc-2" });

        corrected.Outcome.Should().Be(ContractOutcome.Ok);
        corrected.Value!.SignedDocumentId.Should().Be("doc-2");
        corrected.Value.StatusHistory.Should().ContainSingle("attaching a document is not a status change");
    }

    [Fact]
    public async Task A_contract_period_needs_both_dates_in_the_right_order()
    {
        var noEnd = await Service.CreateTermAsync(Manager(), Group, new TermWrite { PeriodStart = PlanYear2027.PeriodStart });
        var backwards = await Service.CreateTermAsync(Manager(), Group, new TermWrite
        {
            PeriodStart = PlanYear2027.PeriodEnd, PeriodEnd = PlanYear2027.PeriodStart,
        });

        noEnd.Outcome.Should().Be(ContractOutcome.Invalid);
        backwards.Outcome.Should().Be(ContractOutcome.Invalid);
        backwards.Errors.Should().Contain(e => e.Contains("end date"));
    }

    // ── The term in force ─────────────────────────────────────────

    [Fact]
    public async Task The_term_in_force_is_the_signed_one_covering_the_date()
    {
        var start = PlanYear2027.PeriodStart!.Value;
        await Service.CreateTermAsync(Manager(), Group, PlanYear2027);

        // A draft is not something to bill or enrol from.
        (await Service.TermInForceAsync(Tenant, Group, start)).Should().BeNull();

        await Service.SetTermStatusAsync(Manager(), Group, start,
            new TermStatusWrite { Status = ContractTermStatus.Signed, SignedDocumentId = "doc-1" });

        (await Service.TermInForceAsync(Tenant, Group, start))!.Id.Should().Be("t1|123456|20270101");
        (await Service.TermInForceAsync(Tenant, Group, new DateTime(2027, 12, 31, 0, 0, 0, DateTimeKind.Utc)))
            .Should().NotBeNull("the last day of the period is covered");
        (await Service.TermInForceAsync(Tenant, Group, new DateTime(2028, 1, 1, 0, 0, 0, DateTimeKind.Utc)))
            .Should().BeNull("the day after it is not");
    }

    [Fact]
    public async Task A_group_with_no_contract_has_no_term_in_force_which_is_an_answer()
    {
        // Which is every group today: a consumer falls back on what it reads
        // now rather than treating this as a failure.
        (await Service.TermInForceAsync(Tenant, Group, _clock.Now)).Should().BeNull();
    }
}
