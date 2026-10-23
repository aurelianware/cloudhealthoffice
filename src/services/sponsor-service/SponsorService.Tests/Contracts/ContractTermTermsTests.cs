using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SponsorService.Contracts;
using SponsorService.Models;
using SponsorService.Tests.Fakes;

namespace SponsorService.Tests.Contracts;

/// <summary>
/// The per-year sections that belong to the contract term rather than to a
/// policy (docs/architecture/plan-configuration.md §9b, step 3b): how the
/// group is billed, and the conditions the quote was written against. The
/// group is billed and underwritten as a whole, which is why these are not on
/// each policy.
///
/// <para>
/// The billing terms are the ones §9b moves off <c>Sponsor.BillingInfo</c>,
/// where one set of terms covered the account however many years it ran.
/// Nothing reads them yet; premium billing and AR move in step 3d.
/// </para>
/// </summary>
public class ContractTermTermsTests
{
    private const string Tenant = "t1";
    private const string Group = "123456";
    private static readonly DateTime Y2027 = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly InMemoryContractStore _store = new();
    private readonly InMemorySponsorRepository _sponsors = new();
    private readonly FixedClock _clock = new(new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc));

    private ContractService Service => new(_store, _sponsors, _clock, NullLogger<ContractService>.Instance);
    private static TestActor Manager() => new("am", Tenant, "config:sponsor");

    public ContractTermTermsTests()
    {
        _sponsors.Put(new Sponsor
        {
            TenantId = Tenant, Id = "s1", GroupNumber = Group, EmployerName = "Acme Manufacturing",
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
    }

    private async Task SeedTermAsync() =>
        (await Service.CreateTermAsync(Manager(), Group, new TermWrite
        {
            PeriodStart = Y2027, PeriodEnd = new DateTime(2027, 12, 31),
        })).Outcome.Should().Be(ContractOutcome.Ok);

    private async Task SignAsync() =>
        (await Service.SetTermStatusAsync(Manager(), Group, Y2027, new TermStatusWrite
        {
            Status = ContractTermStatus.Signed, SignedDocumentId = "doc-1",
        })).Outcome.Should().Be(ContractOutcome.Ok);

    private static BillingTerms ListBill => new()
    {
        Method = BillingMethod.ListBill,
        Frequency = BillingFrequency.Monthly,
        DueDay = 1,
        GracePeriodDays = 30,
        BillingAccountNumber = "ACCT-1",
        PaymentMethod = "ACH",
    };

    /// <summary>
    /// A copy of the list-bill terms with some fields changed. The stored
    /// records are mutable classes rather than records, because they are
    /// documents; this is the <c>with</c> a test wants without reshaping the
    /// model to suit the test.
    /// </summary>
    private static BillingTerms ListBillBut(
        int? dueDay = null, int? gracePeriodDays = null, BillingMethod? method = null,
        BillingFrequency? frequency = null, string? accountNumber = null, string? paymentMethod = null)
    {
        var from = ListBill;
        return new BillingTerms
        {
            Method = method ?? from.Method,
            Frequency = frequency ?? from.Frequency,
            DueDay = dueDay ?? from.DueDay,
            GracePeriodDays = gracePeriodDays ?? from.GracePeriodDays,
            BillingAccountNumber = accountNumber ?? from.BillingAccountNumber,
            PaymentMethod = paymentMethod ?? from.PaymentMethod,
        };
    }

    // ── Entering the terms ────────────────────────────────────────

    [Fact]
    public async Task Billing_terms_and_underwriting_conditions_are_entered_on_the_term()
    {
        await SeedTermAsync();

        var result = await Service.SetTermTermsAsync(Manager(), Group, Y2027, new TermTermsWrite
        {
            Billing = ListBill,
            Underwriting = new UnderwritingConditions
            {
                MinimumParticipationPercent = 75m,
                MinimumEmployerContributionPercent = 50m,
                MinimumEnrolledEmployees = 10,
            },
        });

        result.Outcome.Should().Be(ContractOutcome.Ok, string.Join("; ", result.Errors));
        var term = result.Value!;
        term.Billing!.Method.Should().Be(BillingMethod.ListBill);
        term.Billing.GracePeriodDays.Should().Be(30);
        term.Underwriting!.MinimumParticipationPercent.Should().Be(75m);
    }

    [Fact]
    public async Task Each_year_carries_its_own_billing_terms()
    {
        await SeedTermAsync();
        await Service.SetTermTermsAsync(Manager(), Group, Y2027, new TermTermsWrite { Billing = ListBill });

        (await Service.CreateTermAsync(Manager(), Group, new TermWrite
        {
            PeriodStart = new DateTime(2028, 1, 1), PeriodEnd = new DateTime(2028, 12, 31),
        })).Outcome.Should().Be(ContractOutcome.Ok);
        await Service.SetTermTermsAsync(Manager(), Group, new DateTime(2028, 1, 1), new TermTermsWrite
        {
            Billing = new BillingTerms { Method = BillingMethod.SelfBill, GracePeriodDays = 45, DueDay = 15 },
        });

        var terms = (await Service.ListTermsAsync(Tenant, Group)).Value!;
        terms.Should().HaveCount(2);
        terms[0].Billing!.Method.Should().Be(BillingMethod.ListBill);
        terms[1].Billing!.Method.Should().Be(BillingMethod.SelfBill);
        terms[1].Billing!.GracePeriodDays.Should().Be(45);
    }

    [Fact]
    public async Task A_term_that_does_not_exist_is_not_found()
    {
        var result = await Service.SetTermTermsAsync(Manager(), Group, Y2027, new TermTermsWrite { Billing = ListBill });

        result.Outcome.Should().Be(ContractOutcome.NotFound);
    }

    [Fact]
    public async Task An_ended_terms_billing_cannot_be_changed()
    {
        await SeedTermAsync();
        await Service.SetTermStatusAsync(Manager(), Group, Y2027, new TermStatusWrite
        {
            Status = ContractTermStatus.Ended, Reason = "the group did not take up the quote",
        });

        var result = await Service.SetTermTermsAsync(Manager(), Group, Y2027, new TermTermsWrite { Billing = ListBill });

        result.Outcome.Should().Be(ContractOutcome.Conflict);
        result.Errors.Should().ContainSingle().Which.Should().Contain("has ended");
    }

    // ── What signature settles, and what it does not ──────────────

    [Fact]
    public async Task After_signature_the_agreed_billing_terms_are_settled()
    {
        await SeedTermAsync();
        await Service.SetTermTermsAsync(Manager(), Group, Y2027, new TermTermsWrite { Billing = ListBill });
        await SignAsync();

        var result = await Service.SetTermTermsAsync(Manager(), Group, Y2027, new TermTermsWrite
        {
            Billing = ListBillBut(gracePeriodDays: 60),
        });

        result.Outcome.Should().Be(ContractOutcome.Invalid);
        result.Errors.Should().ContainSingle().Which.Should().Contain("Open a new contract period");

        var stored = (await Service.ListTermsAsync(Tenant, Group)).Value![0];
        stored.Billing!.GracePeriodDays.Should().Be(30);
    }

    [Fact]
    public async Task After_signature_the_remittance_details_can_still_be_corrected()
    {
        await SeedTermAsync();
        await Service.SetTermTermsAsync(Manager(), Group, Y2027, new TermTermsWrite { Billing = ListBill });
        await SignAsync();

        // A group that changes bank in March should not need a new contract
        // period to say where the money now comes from.
        var result = await Service.SetTermTermsAsync(Manager(), Group, Y2027, new TermTermsWrite
        {
            Billing = ListBillBut(accountNumber: "ACCT-2", paymentMethod: "WIRE"),
        });

        result.Outcome.Should().Be(ContractOutcome.Ok, string.Join("; ", result.Errors));
        var term = result.Value!;
        term.Billing!.BillingAccountNumber.Should().Be("ACCT-2");
        term.Billing.PaymentMethod.Should().Be("WIRE");
        // And what was agreed is untouched.
        term.Billing.GracePeriodDays.Should().Be(30);
        term.Billing.Method.Should().Be(BillingMethod.ListBill);
    }

    [Fact]
    public async Task Restating_the_signed_terms_unchanged_is_not_a_change()
    {
        await SeedTermAsync();
        await Service.SetTermTermsAsync(Manager(), Group, Y2027, new TermTermsWrite { Billing = ListBill });
        await SignAsync();

        // A client that round-trips the whole record still works after
        // signature, because nothing it sent differs from what is stored.
        var result = await Service.SetTermTermsAsync(Manager(), Group, Y2027, new TermTermsWrite { Billing = ListBill });

        result.Outcome.Should().Be(ContractOutcome.Ok, string.Join("; ", result.Errors));
    }

    [Fact]
    public async Task After_signature_the_underwriting_conditions_are_settled_too()
    {
        await SeedTermAsync();
        await Service.SetTermTermsAsync(Manager(), Group, Y2027, new TermTermsWrite
        {
            Underwriting = new UnderwritingConditions { MinimumParticipationPercent = 75m },
        });
        await SignAsync();

        var result = await Service.SetTermTermsAsync(Manager(), Group, Y2027, new TermTermsWrite
        {
            Underwriting = new UnderwritingConditions { MinimumParticipationPercent = 50m },
        });

        result.Outcome.Should().Be(ContractOutcome.Invalid);
        result.Errors.Should().ContainSingle().Which.Should().Contain("written against are settled");
    }

    [Fact]
    public async Task Billing_terms_entered_for_the_first_time_after_signature_are_refused()
    {
        await SeedTermAsync();
        await SignAsync();

        // A signed contract with no billing terms is a gap, and filling it in
        // afterwards would be stating terms nobody signed. Opening a period
        // puts it on the record instead.
        var result = await Service.SetTermTermsAsync(Manager(), Group, Y2027, new TermTermsWrite { Billing = ListBill });

        result.Outcome.Should().Be(ContractOutcome.Invalid);
        result.Errors.Should().ContainSingle().Which.Should().Contain("Open a new contract period");
    }

    [Fact]
    public async Task A_term_in_underwriting_is_still_being_worked_on()
    {
        await SeedTermAsync();
        await Service.SetTermStatusAsync(Manager(), Group, Y2027, new TermStatusWrite
        {
            Status = ContractTermStatus.InUnderwriting, Reason = "participation is 68%",
        });

        var result = await Service.SetTermTermsAsync(Manager(), Group, Y2027, new TermTermsWrite
        {
            Billing = ListBillBut(gracePeriodDays: 45),
            Underwriting = new UnderwritingConditions
            {
                MinimumParticipationPercent = 65m, Notes = "relaxed to 65% for the first year",
            },
        });

        result.Outcome.Should().Be(ContractOutcome.Ok, string.Join("; ", result.Errors));
        result.Value!.Underwriting!.Notes.Should().Be("relaxed to 65% for the first year");
    }

    // ── Validation ────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    public async Task A_due_day_that_is_not_a_day_of_the_month_is_refused(int day)
    {
        await SeedTermAsync();

        var result = await Service.SetTermTermsAsync(Manager(), Group, Y2027, new TermTermsWrite
        {
            Billing = ListBillBut(dueDay: day),
        });

        result.Outcome.Should().Be(ContractOutcome.Invalid);
        result.Errors.Should().ContainSingle().Which.Should().Contain("day of the month");
    }

    [Fact]
    public async Task A_grace_period_outside_the_range_the_sponsor_record_allowed_is_refused()
    {
        await SeedTermAsync();

        var result = await Service.SetTermTermsAsync(Manager(), Group, Y2027, new TermTermsWrite
        {
            Billing = ListBillBut(gracePeriodDays: 120),
        });

        result.Outcome.Should().Be(ContractOutcome.Invalid);
        result.Errors.Should().ContainSingle().Which.Should().Contain("between 0 and 90 days");
    }

    [Fact]
    public async Task A_minimum_that_is_not_a_percentage_is_refused()
    {
        await SeedTermAsync();

        var result = await Service.SetTermTermsAsync(Manager(), Group, Y2027, new TermTermsWrite
        {
            Underwriting = new UnderwritingConditions
            {
                MinimumParticipationPercent = 150m,
                MinimumEmployerContributionPercent = -1m,
            },
        });

        result.Outcome.Should().Be(ContractOutcome.Invalid);
        result.Errors.Should().HaveCount(2);
        result.Errors.Should().Contain(e => e.Contains("minimum participation"));
        result.Errors.Should().Contain(e => e.Contains("minimum employer contribution"));
    }

    // ── The due date ──────────────────────────────────────────────

    [Theory]
    [InlineData(1, 2027, 2, 1)]
    [InlineData(15, 2027, 2, 15)]
    [InlineData(31, 2027, 2, 28)]
    [InlineData(31, 2028, 2, 29)]
    [InlineData(31, 2027, 4, 30)]
    [InlineData(30, 2027, 2, 28)]
    public void A_due_day_past_the_end_of_a_month_falls_on_its_last_day(
        int dueDay, int year, int month, int expectedDay)
    {
        // A due day of 31 means the end of the month, not a date that does not
        // exist. Clamping it here is what keeps every consumer from getting
        // February wrong in its own way.
        var billing = new BillingTerms { DueDay = dueDay };

        billing.DueDateIn(year, month).Should().Be(new DateTime(year, month, expectedDay));
    }

    [Fact]
    public async Task A_concurrent_edit_fails_rather_than_being_undone()
    {
        await SeedTermAsync();

        _store.BeforeReplaceTerm = () => _store.BumpTermRevision(Tenant, "t1|123456|20270101");

        var result = await Service.SetTermTermsAsync(Manager(), Group, Y2027, new TermTermsWrite { Billing = ListBill });

        result.Outcome.Should().Be(ContractOutcome.Conflict);
    }
}
