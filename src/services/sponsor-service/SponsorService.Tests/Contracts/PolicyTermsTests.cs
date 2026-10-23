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
/// A policy's terms, versioned per contract year
/// (docs/architecture/plan-configuration.md §9b; step 3b of the §12 build
/// order). Nothing reads these yet — the consumers move one at a time in 3d.
///
/// <para>
/// What these cover is the two things that make the versioning worth having:
/// that resolving a date goes <b>through</b> the contract term rather than
/// past it, and that the several reasons for finding nothing are told apart.
/// A group with no contract may fall back on what it reads today; a contract
/// with a hole in it may not, because last year's rates are not this year's.
/// Then the writes a caller could otherwise make that no consumer could
/// recover from: a rate table missing a household shape, terms for a class
/// nobody can be placed in, and a waiting period or dependent age limit
/// outside what federal law allows.
/// </para>
/// </summary>
public class PolicyTermsTests
{
    private const string Tenant = "t1";
    private const string Group = "123456";
    private const string Policy = "123456-0001";

    private static readonly DateTime Y2027 = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Y2028 = new(2028, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly InMemoryContractStore _store = new();
    private readonly InMemorySponsorRepository _sponsors = new();
    private readonly FixedClock _clock = new(new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc));

    private ContractService Contracts => new(_store, _sponsors, _clock, NullLogger<ContractService>.Instance);

    private PolicyTermsService Service =>
        new(_store, Contracts, _clock, NullLogger<PolicyTermsService>.Instance);

    private static TestActor Manager() => new("am", Tenant, "config:sponsor");

    public PolicyTermsTests()
    {
        _sponsors.Put(new Sponsor
        {
            TenantId = Tenant, Id = "s1", GroupNumber = Group, EmployerName = "Acme Manufacturing",
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
    }

    /// <summary>A policy with an hourly and a salaried class, offered from 2027.</summary>
    private async Task SeedPolicyAsync() =>
        (await Contracts.CreatePolicyAsync(Manager(), Group, new PolicyWrite
        {
            PolicyNumber = Policy,
            Name = "PPO 500",
            BenefitPlanId = "PLAN-PPO-500",
            EffectiveFrom = Y2027,
            Classes =
            [
                new SponsorClass { Code = "HOURLY", Name = "Hourly employees" },
                new SponsorClass { Code = "SALARIED", Name = "Salaried employees" },
            ],
        })).Outcome.Should().Be(ContractOutcome.Ok);

    /// <summary>A contract period, left as a draft unless a test signs it.</summary>
    private async Task SeedTermAsync(DateTime periodStart) =>
        (await Contracts.CreateTermAsync(Manager(), Group, new TermWrite
        {
            PeriodStart = periodStart,
            PeriodEnd = periodStart.AddYears(1).AddDays(-1),
        })).Outcome.Should().Be(ContractOutcome.Ok);

    private async Task SignAsync(DateTime periodStart) =>
        (await Contracts.SetTermStatusAsync(Manager(), Group, periodStart, new TermStatusWrite
        {
            Status = ContractTermStatus.Signed, SignedDocumentId = "doc-1",
        })).Outcome.Should().Be(ContractOutcome.Ok);

    private static RateTable ThreeTier(decimal employee, decimal plusOne, decimal family) => new()
    {
        Structure = TierStructure.ThreeTier,
        Tiers =
        [
            new TierRate { Tier = RateTier.Employee, Amount = employee },
            new TierRate { Tier = RateTier.EmployeePlusOne, Amount = plusOne },
            new TierRate { Tier = RateTier.Family, Amount = family },
        ],
    };

    private static PolicyTermsWrite Terms => new()
    {
        Rates = ThreeTier(450m, 900m, 1300m),
        Enrollment = new EnrollmentTerms { WaitingPeriodDays = 60, EffectiveDateRule = EffectiveDateRule.FirstOfMonthAfterWaitingPeriod },
        Termination = new TerminationTerms { Rule = TerminationRule.EndOfMonth },
    };

    // ── Entering a year's terms ───────────────────────────────────

    [Fact]
    public async Task Terms_are_entered_against_a_contract_period()
    {
        await SeedPolicyAsync();
        await SeedTermAsync(Y2027);

        var result = await Service.UpsertAsync(Manager(), Group, Policy, Y2027, Terms);

        result.Outcome.Should().Be(ContractOutcome.Ok, string.Join("; ", result.Errors));
        var terms = result.Value!;
        terms.Id.Should().Be("t1|123456|123456-0001|20270101");
        terms.PeriodStart.Should().Be(Y2027.Date);
        terms.Rates!.For(RateTier.EmployeePlusOne).Should().Be(900m);
    }

    [Fact]
    public async Task Terms_need_a_contract_period_to_belong_to()
    {
        await SeedPolicyAsync();

        // §9b's order of events: a renewal opens a draft term, and the terms of
        // each policy are entered into it.
        var result = await Service.UpsertAsync(Manager(), Group, Policy, Y2027, Terms);

        result.Outcome.Should().Be(ContractOutcome.NotFound);
        result.Errors.Should().ContainSingle().Which.Should().Contain("Open the term first");
    }

    [Fact]
    public async Task A_renewal_is_a_new_version_and_the_policy_does_not_move()
    {
        await SeedPolicyAsync();
        await SeedTermAsync(Y2027);
        await SeedTermAsync(Y2028);

        await Service.UpsertAsync(Manager(), Group, Policy, Y2027, Terms);
        await Service.UpsertAsync(Manager(), Group, Policy, Y2028, Terms with { Rates = ThreeTier(480m, 960m, 1390m) });

        var versions = (await Service.ListAsync(Tenant, Group, Policy)).Value!;
        versions.Should().HaveCount(2);
        versions.Select(v => v.PeriodStart).Should().BeInAscendingOrder();
        versions[0].Rates!.For(RateTier.Employee).Should().Be(450m);
        versions[1].Rates!.For(RateTier.Employee).Should().Be(480m);

        // The policy itself is untouched: same id, same number, same plan.
        var policy = (await Contracts.GetPolicyAsync(Tenant, Group, Policy)).Value!;
        policy.Id.Should().Be("t1|123456|123456-0001");
        policy.BenefitPlanId.Should().Be("PLAN-PPO-500");
    }

    [Fact]
    public async Task A_signed_terms_period_is_settled_and_not_edited_in_place()
    {
        await SeedPolicyAsync();
        await SeedTermAsync(Y2027);
        await Service.UpsertAsync(Manager(), Group, Policy, Y2027, Terms);
        await SignAsync(Y2027);

        var result = await Service.UpsertAsync(Manager(), Group, Policy, Y2027, Terms with { Rates = ThreeTier(1m, 2m, 3m) });

        result.Outcome.Should().Be(ContractOutcome.Conflict);
        result.Errors.Should().ContainSingle().Which.Should().Contain("new contract period");

        // And the agreed rates are still the agreed rates.
        (await Service.GetAsync(Tenant, Group, Policy, Y2027)).Value!
            .Rates!.For(RateTier.Employee).Should().Be(450m);
    }

    [Fact]
    public async Task A_draft_period_can_be_restated_while_it_is_being_worked_on()
    {
        await SeedPolicyAsync();
        await SeedTermAsync(Y2027);
        await Service.UpsertAsync(Manager(), Group, Policy, Y2027, Terms);

        var result = await Service.UpsertAsync(Manager(), Group, Policy, Y2027, new PolicyTermsWrite
        {
            Rates = ThreeTier(460m, 920m, 1320m),
        });

        result.Outcome.Should().Be(ContractOutcome.Ok, string.Join("; ", result.Errors));
        result.Value!.Rates!.For(RateTier.Employee).Should().Be(460m);
        // What the caller did not restate is kept, rather than being cleared.
        result.Value.Enrollment!.WaitingPeriodDays.Should().Be(60);
    }

    [Fact]
    public async Task Another_tenants_group_is_not_found_rather_than_empty()
    {
        await SeedPolicyAsync();
        await SeedTermAsync(Y2027);
        await Service.UpsertAsync(Manager(), Group, Policy, Y2027, Terms);

        var result = await Service.ListAsync("t2", Group, Policy);

        result.Outcome.Should().Be(ContractOutcome.NotFound);
    }

    [Fact]
    public async Task A_concurrent_edit_fails_rather_than_being_undone()
    {
        await SeedPolicyAsync();
        await SeedTermAsync(Y2027);
        await Service.UpsertAsync(Manager(), Group, Policy, Y2027, Terms);

        _store.BeforeReplacePolicyTerms = () =>
            _store.BumpPolicyTermsRevision(Tenant, "t1|123456|123456-0001|20270101");

        var result = await Service.UpsertAsync(Manager(), Group, Policy, Y2027, Terms with { Rates = ThreeTier(1m, 2m, 3m) });

        result.Outcome.Should().Be(ContractOutcome.Conflict);
    }

    // ── Resolving a date ──────────────────────────────────────────

    [Fact]
    public async Task Resolving_a_date_goes_through_the_contract_term()
    {
        await SeedPolicyAsync();
        await SeedTermAsync(Y2027);
        await Service.UpsertAsync(Manager(), Group, Policy, Y2027, Terms);
        await SignAsync(Y2027);

        var resolved = await Service.ResolveAsync(Tenant, Group, Policy, new DateTime(2027, 6, 15));

        resolved.Outcome.Should().Be(TermsResolutionOutcome.Resolved);
        resolved.ContractTerm!.PeriodStart.Should().Be(Y2027.Date);
        resolved.Terms!.Rates!.For(RateTier.Family).Should().Be(1300m);
    }

    [Fact]
    public async Task A_draft_years_rates_are_a_quote_and_not_in_force()
    {
        await SeedPolicyAsync();
        await SeedTermAsync(Y2027);
        await Service.UpsertAsync(Manager(), Group, Policy, Y2027, Terms);
        // Left as a draft: nobody has signed anything.

        var resolved = await Service.ResolveAsync(Tenant, Group, Policy, new DateTime(2027, 6, 15));

        resolved.Outcome.Should().Be(TermsResolutionOutcome.NoContractTerm);
        resolved.Terms.Should().BeNull();
    }

    [Fact]
    public async Task No_contract_at_all_is_an_answer_a_consumer_may_fall_back_on()
    {
        await SeedPolicyAsync();

        var resolved = await Service.ResolveAsync(Tenant, Group, Policy, new DateTime(2027, 6, 15));

        resolved.Outcome.Should().Be(TermsResolutionOutcome.NoContractTerm);
        resolved.Policy.Should().NotBeNull("the policy exists; it is the contract that does not");
    }

    [Fact]
    public async Task A_contract_with_no_terms_entered_is_told_apart_from_no_contract()
    {
        await SeedPolicyAsync();
        await SeedTermAsync(Y2027);
        await SignAsync(Y2027);
        // The contract is signed and nobody entered this policy's rates.

        var resolved = await Service.ResolveAsync(Tenant, Group, Policy, new DateTime(2027, 6, 15));

        // Not NoContractTerm: there is a contract, and a consumer that fell
        // back here would bill last year's rates under this year's agreement.
        resolved.Outcome.Should().Be(TermsResolutionOutcome.TermsNotEntered);
        resolved.ContractTerm!.PeriodStart.Should().Be(Y2027.Date);
        resolved.Terms.Should().BeNull();
    }

    [Fact]
    public async Task A_policy_that_is_not_offered_on_the_date_is_told_apart_too()
    {
        await SeedPolicyAsync();
        await SeedTermAsync(Y2027);
        await Service.UpsertAsync(Manager(), Group, Policy, Y2027, Terms);
        await SignAsync(Y2027);

        // The policy is replaced rather than continued: it ends the day before
        // the renewal (§9b).
        (await Contracts.UpdatePolicyAsync(Manager(), Group, Policy, new PolicyWrite
        {
            EffectiveTo = new DateTime(2027, 3, 31),
        })).Outcome.Should().Be(ContractOutcome.Ok);

        var resolved = await Service.ResolveAsync(Tenant, Group, Policy, new DateTime(2027, 6, 15));

        resolved.Outcome.Should().Be(TermsResolutionOutcome.PolicyNotOffered);
        resolved.ContractTerm.Should().NotBeNull("the contract is in force; it is this policy that is not sold");
    }

    [Fact]
    public async Task Each_year_resolves_to_its_own_version()
    {
        await SeedPolicyAsync();
        foreach (var year in new[] { Y2027, Y2028 })
        {
            await SeedTermAsync(year);
            await Service.UpsertAsync(Manager(), Group, Policy, year,
                Terms with { Rates = ThreeTier(year == Y2027 ? 450m : 480m, 900m, 1300m) });
            await SignAsync(year);
        }

        (await Service.ResolveAsync(Tenant, Group, Policy, new DateTime(2027, 12, 31)))
            .Terms!.Rates!.For(RateTier.Employee).Should().Be(450m);
        (await Service.ResolveAsync(Tenant, Group, Policy, new DateTime(2028, 1, 1)))
            .Terms!.Rates!.For(RateTier.Employee).Should().Be(480m);
    }

    // ── Rates ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_rate_table_quotes_exactly_the_tiers_its_structure_calls_for()
    {
        await SeedPolicyAsync();
        await SeedTermAsync(Y2027);

        var result = await Service.UpsertAsync(Manager(), Group, Policy, Y2027, new PolicyTermsWrite
        {
            Rates = new RateTable
            {
                Structure = TierStructure.FourTier,
                Tiers =
                [
                    new TierRate { Tier = RateTier.Employee, Amount = 450m },
                    new TierRate { Tier = RateTier.Family, Amount = 1300m },
                    // No employee-plus-spouse, no employee-plus-children, and a
                    // tier belonging to the three-tier structure.
                    new TierRate { Tier = RateTier.EmployeePlusOne, Amount = 900m },
                ],
            },
        });

        result.Outcome.Should().Be(ContractOutcome.Invalid);
        result.Errors.Should().HaveCount(3);
        result.Errors.Should().Contain(e => e.Contains("employee plus spouse"));
        result.Errors.Should().Contain(e => e.Contains("employee plus children"));
        result.Errors.Should().Contain(e => e.Contains("not a tier of a four-tier structure"));
    }

    [Theory]
    [InlineData(TierStructure.TwoTier, 2)]
    [InlineData(TierStructure.ThreeTier, 3)]
    [InlineData(TierStructure.FourTier, 4)]
    public void Each_structure_names_its_own_tiers(TierStructure structure, int count)
    {
        var tiers = RateTable.TiersOf(structure);

        tiers.Should().HaveCount(count);
        tiers.Should().OnlyHaveUniqueItems();
        tiers.Should().Contain(RateTier.Employee).And.Contain(RateTier.Family);
    }

    [Fact]
    public async Task A_negative_rate_is_refused()
    {
        await SeedPolicyAsync();
        await SeedTermAsync(Y2027);

        var result = await Service.UpsertAsync(Manager(), Group, Policy, Y2027, new PolicyTermsWrite
        {
            Rates = ThreeTier(450m, -900m, 1300m),
        });

        result.Outcome.Should().Be(ContractOutcome.Invalid);
        result.Errors.Should().Contain(e => e.Contains("cannot be negative"));
    }

    // ── Classes ───────────────────────────────────────────────────

    [Fact]
    public async Task A_class_may_have_its_own_rates_and_eligibility()
    {
        await SeedPolicyAsync();
        await SeedTermAsync(Y2027);

        var result = await Service.UpsertAsync(Manager(), Group, Policy, Y2027, Terms with
        {
            ClassTerms =
            [
                new ClassTerms
                {
                    ClassCode = "hourly",
                    Rates = ThreeTier(500m, 1000m, 1450m),
                    Eligibility = new EligibilityTerms { MinimumHoursPerWeek = 30 },
                },
            ],
        });

        result.Outcome.Should().Be(ContractOutcome.Ok, string.Join("; ", result.Errors));
        var terms = result.Value!;
        terms.ClassTerms.Should().ContainSingle().Which.ClassCode.Should().Be("HOURLY");
        terms.RatesFor("HOURLY")!.For(RateTier.Employee).Should().Be(500m);
        // A class that says nothing of its own falls back on the policy's.
        terms.RatesFor("SALARIED")!.For(RateTier.Employee).Should().Be(450m);
        terms.EligibilityFor("HOURLY")!.MinimumHoursPerWeek.Should().Be(30);
    }

    [Fact]
    public async Task Terms_for_a_class_the_policy_does_not_have_are_refused()
    {
        await SeedPolicyAsync();
        await SeedTermAsync(Y2027);

        var result = await Service.UpsertAsync(Manager(), Group, Policy, Y2027, Terms with
        {
            ClassTerms = [new ClassTerms { ClassCode = "UNION", Rates = ThreeTier(1m, 2m, 3m) }],
        });

        // Terms nobody can be placed under would still read as a real answer to
        // anything asking about that class.
        result.Outcome.Should().Be(ContractOutcome.Invalid);
        result.Errors.Should().ContainSingle().Which.Should().Contain("has no class 'UNION'");
    }

    [Fact]
    public async Task One_class_cannot_have_two_sets_of_terms()
    {
        await SeedPolicyAsync();
        await SeedTermAsync(Y2027);

        var result = await Service.UpsertAsync(Manager(), Group, Policy, Y2027, Terms with
        {
            ClassTerms =
            [
                new ClassTerms { ClassCode = "HOURLY", Rates = ThreeTier(1m, 2m, 3m) },
                new ClassTerms { ClassCode = "hourly", Rates = ThreeTier(4m, 5m, 6m) },
            ],
        });

        result.Outcome.Should().Be(ContractOutcome.Invalid);
        result.Errors.Should().Contain(e => e.Contains("two sets of terms"));
    }

    [Fact]
    public async Task An_unknown_class_is_not_the_default_class()
    {
        await SeedPolicyAsync();
        await SeedTermAsync(Y2027);
        var terms = (await Service.UpsertAsync(Manager(), Group, Policy, Y2027, Terms with
        {
            ClassTerms = [new ClassTerms { ClassCode = "HOURLY", Eligibility = new EligibilityTerms { MinimumHoursPerWeek = 30 } }],
        })).Value!;

        // Covers() is how a caller tells "this class has terms" from "the
        // policy-wide terms came back". A member placed in a class nobody wrote
        // terms for must not be rated under rules written for somebody else.
        terms.Covers("HOURLY").Should().BeTrue();
        terms.Covers("UNION").Should().BeFalse();
        terms.Covers("").Should().BeFalse();
    }

    // ── Federal floors ────────────────────────────────────────────

    [Fact]
    public async Task A_waiting_period_longer_than_ninety_days_is_refused()
    {
        await SeedPolicyAsync();
        await SeedTermAsync(Y2027);

        var result = await Service.UpsertAsync(Manager(), Group, Policy, Y2027, Terms with
        {
            Enrollment = new EnrollmentTerms { WaitingPeriodDays = 120 },
        });

        result.Outcome.Should().Be(ContractOutcome.Invalid);
        result.Errors.Should().ContainSingle().Which.Should().Contain("longer than 90 days");
    }

    [Fact]
    public async Task Ninety_days_exactly_is_allowed()
    {
        await SeedPolicyAsync();
        await SeedTermAsync(Y2027);

        var result = await Service.UpsertAsync(Manager(), Group, Policy, Y2027, Terms with
        {
            Enrollment = new EnrollmentTerms { WaitingPeriodDays = ContractFloors.MaxWaitingPeriodDays },
        });

        result.Outcome.Should().Be(ContractOutcome.Ok, string.Join("; ", result.Errors));
    }

    [Fact]
    public async Task A_child_age_limit_below_twenty_six_is_refused_but_a_higher_one_is_not()
    {
        await SeedPolicyAsync();
        await SeedTermAsync(Y2027);

        var low = await Service.UpsertAsync(Manager(), Group, Policy, Y2027, Terms with
        {
            Eligibility = new EligibilityTerms { Dependents = new DependentTerms { ChildAgeLimit = 19 } },
        });
        low.Outcome.Should().Be(ContractOutcome.Invalid);
        low.Errors.Should().ContainSingle().Which.Should().Contain("at least 26");

        // A contract may be more generous than the floor.
        var high = await Service.UpsertAsync(Manager(), Group, Policy, Y2027, Terms with
        {
            Eligibility = new EligibilityTerms { Dependents = new DependentTerms { ChildAgeLimit = 30 } },
        });
        high.Outcome.Should().Be(ContractOutcome.Ok, string.Join("; ", high.Errors));
    }

    [Theory]
    [InlineData("MARRIAGE", 20, 30)]
    [InlineData("BIRTH", 14, 30)]
    [InlineData("LOSS_OF_MEDICAID", 45, 60)]
    [InlineData("LOSS_OF_CHIP", 30, 60)]
    public async Task A_special_enrollment_window_shorter_than_the_federal_floor_is_refused(
        string code, int window, int floor)
    {
        await SeedPolicyAsync();
        await SeedTermAsync(Y2027);

        var result = await Service.UpsertAsync(Manager(), Group, Policy, Y2027, Terms with
        {
            LifeEvents = [new LifeEventTerms { Code = code, ReportingWindowDays = window }],
        });

        result.Outcome.Should().Be(ContractOutcome.Invalid);
        result.Errors.Should().ContainSingle().Which.Should().Contain($"at least {floor} days");
    }

    [Fact]
    public async Task An_event_federal_law_says_nothing_about_is_not_held_to_either_floor()
    {
        await SeedPolicyAsync();
        await SeedTermAsync(Y2027);

        // A plan's own rule for an internal transfer is not special enrolment.
        var result = await Service.UpsertAsync(Manager(), Group, Policy, Y2027, Terms with
        {
            LifeEvents = [new LifeEventTerms { Code = "INTERNAL_TRANSFER", ReportingWindowDays = 7 }],
        });

        result.Outcome.Should().Be(ContractOutcome.Ok, string.Join("; ", result.Errors));
    }

    [Fact]
    public async Task Life_events_are_canonical_and_each_code_appears_once()
    {
        await SeedPolicyAsync();
        await SeedTermAsync(Y2027);

        var result = await Service.UpsertAsync(Manager(), Group, Policy, Y2027, Terms with
        {
            LifeEvents =
            [
                new LifeEventTerms
                {
                    Code = " birth ", ReportingWindowDays = 31,
                    DocumentationRequired = ["birth_certificate", "BIRTH_CERTIFICATE", " "],
                },
            ],
        });

        result.Outcome.Should().Be(ContractOutcome.Ok, string.Join("; ", result.Errors));
        var e = result.Value!.LifeEvents.Should().ContainSingle().Subject;
        e.Code.Should().Be("BIRTH");
        e.DocumentationRequired.Should().Equal("BIRTH_CERTIFICATE");
    }

    [Fact]
    public async Task One_event_cannot_be_listed_twice()
    {
        await SeedPolicyAsync();
        await SeedTermAsync(Y2027);

        var result = await Service.UpsertAsync(Manager(), Group, Policy, Y2027, Terms with
        {
            LifeEvents =
            [
                new LifeEventTerms { Code = "BIRTH", ReportingWindowDays = 30 },
                new LifeEventTerms { Code = "birth", ReportingWindowDays = 60 },
            ],
        });

        result.Outcome.Should().Be(ContractOutcome.Invalid);
        result.Errors.Should().Contain(e => e.Contains("listed twice"));
    }

    // ── The rest of the shape ─────────────────────────────────────

    [Fact]
    public async Task A_contribution_share_is_a_percentage()
    {
        await SeedPolicyAsync();
        await SeedTermAsync(Y2027);

        var result = await Service.UpsertAsync(Manager(), Group, Policy, Y2027, Terms with
        {
            Contribution = new ContributionTerms
            {
                EmployerSharePercentEmployee = 80m,
                EmployerSharePercentDependents = 140m,
            },
        });

        result.Outcome.Should().Be(ContractOutcome.Invalid);
        result.Errors.Should().ContainSingle().Which.Should().Contain("dependent share");
    }

    [Fact]
    public async Task Employee_types_are_canonical_and_deduplicated()
    {
        await SeedPolicyAsync();
        await SeedTermAsync(Y2027);

        var result = await Service.UpsertAsync(Manager(), Group, Policy, Y2027, Terms with
        {
            Eligibility = new EligibilityTerms
            {
                MinimumHoursPerWeek = 30,
                EmployeeTypes = ["full time", "FULL TIME", " ", "seasonal"],
            },
        });

        result.Outcome.Should().Be(ContractOutcome.Ok, string.Join("; ", result.Errors));
        result.Value!.Eligibility!.EmployeeTypes.Should().Equal("FULL TIME", "SEASONAL");
    }

    [Fact]
    public async Task A_rate_table_is_stored_in_its_structures_order_with_nothing_missing()
    {
        await SeedPolicyAsync();
        await SeedTermAsync(Y2027);

        var result = await Service.UpsertAsync(Manager(), Group, Policy, Y2027, new PolicyTermsWrite
        {
            Rates = new RateTable
            {
                Structure = TierStructure.ThreeTier,
                Tiers =
                [
                    new TierRate { Tier = RateTier.Family, Amount = 1300m },
                    new TierRate { Tier = RateTier.Employee, Amount = 450m },
                    new TierRate { Tier = RateTier.EmployeePlusOne, Amount = 900m },
                ],
            },
        });

        result.Outcome.Should().Be(ContractOutcome.Ok, string.Join("; ", result.Errors));
        result.Value!.Rates!.Tiers.Select(t => t.Tier)
            .Should().Equal(RateTier.Employee, RateTier.EmployeePlusOne, RateTier.Family);
    }

    [Fact]
    public async Task A_period_with_no_terms_is_not_found_rather_than_empty()
    {
        await SeedPolicyAsync();
        await SeedTermAsync(Y2027);

        var result = await Service.GetAsync(Tenant, Group, Policy, Y2027);

        result.Outcome.Should().Be(ContractOutcome.NotFound);
        result.Errors.Should().ContainSingle().Which.Should().Contain("no terms entered");
    }
}
