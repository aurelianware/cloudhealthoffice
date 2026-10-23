using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SponsorService.Contracts;
using SponsorService.Models;
using SponsorService.Security;
using SponsorService.Tests.Fakes;

namespace SponsorService.Tests.Contracts;

/// <summary>
/// Resolving a date to the billing terms the contract puts in force, which is
/// what premium billing reads in step 3d
/// (docs/architecture/plan-configuration.md §9b, §12.10).
///
/// <para>
/// The answers are deliberately three, not two. Terms resolved, no contract
/// term covering the date, and a contract term that exists and says nothing
/// about billing. A consumer falls back the same way for the last two, but
/// they are not the same thing to a person: the first is a group nobody has
/// contracted with yet and the second is a contract somebody left half
/// entered.
/// </para>
/// </summary>
public class BillingInForceTests
{
    private const string Tenant = "t1";
    private const string Group = "123456";
    private static readonly DateTime Y2027 = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime InTheYear = new(2027, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    private readonly InMemoryContractStore _store = new();
    private readonly InMemorySponsorRepository _sponsors = new();
    private readonly FixedClock _clock = new(new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc));

    private ContractService Service => new(_store, _sponsors, _clock, NullLogger<ContractService>.Instance);
    private static TestActor Manager() => new("am", Tenant, "config:sponsor");

    public BillingInForceTests()
    {
        _sponsors.Put(new Sponsor
        {
            TenantId = Tenant, Id = "s1", GroupNumber = Group, EmployerName = "Acme Manufacturing",
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
    }

    private static BillingTerms Billing(int dueDay = 15, int graceDays = 45) => new()
    {
        Method = BillingMethod.ListBill,
        Frequency = BillingFrequency.Monthly,
        DueDay = dueDay,
        GracePeriodDays = graceDays,
        BillingAccountNumber = "1234567890",
        PaymentMethod = "ACH",
    };

    private async Task SeedSignedTermAsync(BillingTerms? billing)
    {
        (await Service.CreateTermAsync(Manager(), Group, new TermWrite
        {
            PeriodStart = Y2027, PeriodEnd = new DateTime(2027, 12, 31),
        })).Outcome.Should().Be(ContractOutcome.Ok);

        if (billing is not null)
        {
            (await Service.SetTermTermsAsync(Manager(), Group, Y2027, new TermTermsWrite { Billing = billing }))
                .Outcome.Should().Be(ContractOutcome.Ok);
        }

        (await Service.SetTermStatusAsync(Manager(), Group, Y2027, new TermStatusWrite
        {
            Status = ContractTermStatus.Signed, SignedDocumentId = "doc-1",
        })).Outcome.Should().Be(ContractOutcome.Ok);
    }

    [Fact]
    public async Task A_signed_term_covering_the_date_gives_its_billing_terms()
    {
        await SeedSignedTermAsync(Billing(dueDay: 15, graceDays: 45));

        var resolved = await Service.BillingInForceAsync(Tenant, Group, InTheYear);

        resolved.Outcome.Should().Be(BillingResolutionOutcome.Resolved);
        resolved.Billing!.DueDay.Should().Be(15);
        resolved.Billing.GracePeriodDays.Should().Be(45);
        resolved.ContractTerm!.PeriodStart.Should().Be(Y2027);
    }

    [Fact]
    public async Task A_group_with_no_contract_term_resolves_to_no_contract_term()
    {
        var resolved = await Service.BillingInForceAsync(Tenant, Group, InTheYear);

        resolved.Outcome.Should().Be(BillingResolutionOutcome.NoContractTerm);
        resolved.Billing.Should().BeNull();
        resolved.ContractTerm.Should().BeNull();
    }

    [Fact]
    public async Task A_date_outside_every_contract_period_resolves_to_no_contract_term()
    {
        await SeedSignedTermAsync(Billing());

        var resolved = await Service.BillingInForceAsync(Tenant, Group, new DateTime(2028, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        resolved.Outcome.Should().Be(BillingResolutionOutcome.NoContractTerm);
    }

    [Fact]
    public async Task A_term_still_in_underwriting_is_not_in_force()
    {
        // Only a signed or active term is in force, which is the same test the
        // rest of step 3 uses: an unsigned contract is a proposal.
        (await Service.CreateTermAsync(Manager(), Group, new TermWrite
        {
            PeriodStart = Y2027, PeriodEnd = new DateTime(2027, 12, 31),
        })).Outcome.Should().Be(ContractOutcome.Ok);
        (await Service.SetTermTermsAsync(Manager(), Group, Y2027, new TermTermsWrite { Billing = Billing() }))
            .Outcome.Should().Be(ContractOutcome.Ok);

        var resolved = await Service.BillingInForceAsync(Tenant, Group, InTheYear);

        resolved.Outcome.Should().Be(BillingResolutionOutcome.NoContractTerm);
    }

    [Fact]
    public async Task A_signed_term_with_no_billing_entered_is_its_own_answer()
    {
        // Not NoContractTerm: there is a contract, and the hole in it is worth
        // naming rather than looking like a group nobody contracted with.
        await SeedSignedTermAsync(billing: null);

        var resolved = await Service.BillingInForceAsync(Tenant, Group, InTheYear);

        resolved.Outcome.Should().Be(BillingResolutionOutcome.BillingNotEntered);
        resolved.Billing.Should().BeNull();
        resolved.ContractTerm!.PeriodStart.Should().Be(Y2027);
    }
}

/// <summary>
/// The billing account number on a contract term, written out of an HTTP
/// response.
///
/// <para>
/// The sponsor record's own <c>BillingInfo</c> has been masked to its last four
/// since the field was encrypted. A contract term carries the same number, so
/// writing it in the clear there would have meant the masking decided nothing:
/// a caller who wanted the number would have read the contract instead. These
/// pin the two paths to the same rule.
/// </para>
/// </summary>
public class MaskedBillingTermsTests
{
    private static readonly JsonSerializerOptions Masked = Options(new MaskedBillingTermsJsonConverter());
    private static readonly JsonSerializerOptions Unmasked = Options();

    /// <summary>
    /// The shape the service's own MVC options have: camel case, enums as
    /// strings, and the masking converter first where it applies.
    /// </summary>
    private static JsonSerializerOptions Options(params System.Text.Json.Serialization.JsonConverter[] converters)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        foreach (var converter in converters) options.Converters.Add(converter);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return options;
    }

    private static BillingTerms Terms(string? account) => new()
    {
        Method = BillingMethod.SelfBill,
        Frequency = BillingFrequency.Quarterly,
        DueDay = 10,
        GracePeriodDays = 31,
        BillingAccountNumber = account,
        PaymentMethod = "Wire",
    };

    [Fact]
    public void A_response_shows_the_last_four_and_never_the_number()
    {
        var json = JsonSerializer.Serialize(Terms("9876543210"), Masked);

        json.Should().NotContain("9876543210");
        json.Should().Contain("\"billingAccountNumberLast4\":\"3210\"");
        json.Should().NotContain("\"billingAccountNumber\":");
    }

    [Fact]
    public void The_terms_a_consumer_actually_bills_on_still_come_through()
    {
        var json = JsonSerializer.Serialize(Terms("9876543210"), Masked);

        json.Should().Contain("\"dueDay\":10").And.Contain("\"gracePeriodDays\":31");
        json.Should().Contain("\"paymentMethod\":\"Wire\"");
        json.Should().Contain("\"method\":\"SelfBill\"").And.Contain("\"frequency\":\"Quarterly\"");
    }

    [Fact]
    public void A_still_encrypted_number_shows_nothing_rather_than_ciphertext()
    {
        // This path never decrypts, so the last four of an "enc:..." value
        // would be four characters of ciphertext presented as an account number.
        var json = JsonSerializer.Serialize(Terms("enc:AQAAAN0Mn"), Masked);

        json.Should().NotContain("enc:");
        json.Should().Contain("\"billingAccountNumberLast4\":null");
    }

    [Fact]
    public void No_account_number_is_no_last_four()
    {
        JsonSerializer.Serialize(Terms(null), Masked)
            .Should().Contain("\"billingAccountNumberLast4\":null");
    }

    [Fact]
    public void A_request_body_is_read_unchanged_so_the_number_can_still_be_set()
    {
        var body = """
            { "method": "SelfBill", "frequency": "Quarterly", "dueDay": 10,
              "gracePeriodDays": 31, "billingAccountNumber": "9876543210", "paymentMethod": "Wire" }
            """;

        var terms = JsonSerializer.Deserialize<BillingTerms>(body, Masked);

        terms!.BillingAccountNumber.Should().Be("9876543210");
        terms.DueDay.Should().Be(10);
    }

    [Fact]
    public void Storage_is_not_masked_because_it_does_not_use_these_options()
    {
        // The guard against the masking creeping into the write path: what
        // goes to Mongo is serialized without this converter, and it keeps the
        // number.
        JsonSerializer.Serialize(Terms("9876543210"), Unmasked).Should().Contain("9876543210");
    }
}
