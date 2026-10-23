using CapitationService.Models;
using CapitationService.Repositories;
using CapitationService.Services;
using CapitationService.Tests.Support;
using CloudHealthOffice.PlanConfiguration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CapitationService.Tests.Services;

/// <summary>
/// Which legal entity the provider credits are paid by
/// (plan-configuration.md §12.3). The debit side's counterpart in
/// premium-billing; the reasoning is the same and the stakes are a provider's
/// deposit rather than a sponsor's draw.
///
/// <para>
/// A provider reconciles a deposit against the entity that sent it, so the
/// carrier is asked first and the deployment's <c>Nacha:*</c> values stand
/// wherever it states nothing. When tenant-service cannot be reached the
/// release fails: a carrier that states nothing and a carrier that could not be
/// read are indistinguishable here, and only the first is safe to pay under the
/// configured entity.
/// </para>
/// </summary>
public class CarrierOriginationTests
{
    private const string Tenant = "tenant-1";

    private readonly Mock<ICapitationDisbursementRepository> _disbursementRepo = new();
    private readonly Mock<ICapitationStatementRepository> _statementRepo = new();
    private readonly Mock<ICapitationRunRepository> _runRepo = new();
    private readonly Mock<INachaCreditFileService> _nachaService = new();
    private readonly Mock<IHttpClientFactory> _httpClientFactory = new();
    private readonly RecordingNachaDispatcher _dispatcher = new();
    private readonly InMemoryCarrierDirectory _carriers = new();
    private readonly List<NachaCreditFileOptions> _built = [];

    private static readonly CarrierIdentity FilledIn = new()
    {
        Id = $"{Tenant}|HMO",
        Code = "HMO",
        Name = "the HMO",
        LegalName = "ACME HEALTH PLAN OF TEXAS INC",
        NachaImmediateOrigin = "1742938475",
        NachaCompanyIdentification = "1742938475",
        IsDefault = true,
    };

    public CarrierOriginationTests()
    {
        ReleaseClaims.Uncontended(_statementRepo, _disbursementRepo);

        var disbursement = new CapitationDisbursement
        {
            Id = "d1", TenantId = Tenant, StatementId = "stmt-d1", ProviderNPI = "1234567890",
            ProviderName = "Dr. Chen", Method = DisbursementMethod.NachaCredit, Amount = 5000,
            Status = DisbursementStatus.Pending,
        };
        _disbursementRepo.Setup(r => r.GetByStatusAsync(DisbursementStatus.Pending)).ReturnsAsync([disbursement]);
        _disbursementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationDisbursement>()))
            .ReturnsAsync((CapitationDisbursement d) => d);
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-d1")).ReturnsAsync(new CapitationStatement
        {
            Id = "stmt-d1", ProviderNPI = disbursement.ProviderNPI, NetPayable = disbursement.Amount,
            Status = CapitationStatementStatus.PaymentInitiated, CreatedBy = "maker-1",
        });

        var handler = new MockHttpMessageHandler<ProviderBankAccountDto>(_ => new ProviderBankAccountDto
        {
            EftEnabled = true, RoutingNumber = "091000019", AccountNumber = "123456789",
            AccountHolderName = "TEST PROVIDER",
        });
        _httpClientFactory
            .Setup(f => f.CreateClient("ProviderService"))
            .Returns(new HttpClient(handler) { BaseAddress = new Uri("http://provider-service") });

        _nachaService
            .Setup(s => s.GenerateNachaCreditFile(It.IsAny<List<NachaCreditEntryDetail>>(), It.IsAny<NachaCreditFileOptions>()))
            .Callback<List<NachaCreditEntryDetail>, NachaCreditFileOptions>((entries, options) =>
            {
                _built.Add(options);
                for (var i = 0; i < entries.Count; i++) entries[i].TraceNumber = $"091000010000{i + 1}";
            })
            .Returns(new GeneratedNachaCreditFile { FileReference = "NACHA-CR-TEST", EntryCount = 1, TotalAmount = 5000 });
    }

    [Fact]
    public async Task A_carrier_that_states_an_origination_is_what_the_credits_are_paid_under()
    {
        _carriers.Add(Tenant, FilledIn);

        await Service().GenerateNachaCreditFileAsync("releaser-1");

        var options = _built.Should().ContainSingle().Subject;
        options.ImmediateOrigin.Should().Be("1742938475");
        options.CompanyId.Should().Be("1742938475");
        options.CompanyName.Should().Be("ACME HEALTH PLAN OF TEXAS INC");
        options.CompanyEntryDescription.Should().Be("CAPITATION", "what the credit is for is not the carrier's business");
    }

    [Fact]
    public async Task A_carrier_that_states_nothing_leaves_the_configured_origination_in_place()
    {
        _carriers.Add(Tenant, new CarrierIdentity
        {
            Id = $"{Tenant}|DEFAULT", Code = "DEFAULT", Name = "Acme Health", IsDefault = true,
        });

        await Service().GenerateNachaCreditFileAsync("releaser-1");

        var options = _built.Should().ContainSingle().Subject;
        options.ImmediateOrigin.Should().Be("1234567890");
        options.CompanyName.Should().Be("HEALTH PLAN");
    }

    [Fact]
    public async Task A_host_with_no_carrier_directory_pays_exactly_as_before()
    {
        await Service(withDirectory: false).GenerateNachaCreditFileAsync("releaser-1");

        _built.Should().ContainSingle().Which.ImmediateOrigin.Should().Be("1234567890");
        _carriers.Calls.Should().Be(0);
    }

    [Fact]
    public async Task An_unreachable_tenant_service_pays_nothing_and_leaves_the_disbursements_pending()
    {
        _carriers.Unavailable = true;

        var release = () => Service().GenerateNachaCreditFileAsync("releaser-1");

        await release.Should().ThrowAsync<HttpRequestException>();
        _built.Should().BeEmpty("no file is built when the paying entity is not known");
        _dispatcher.Sent.Should().BeEmpty();
        _disbursementRepo.Verify(r => r.ReleaseClaimAsync("d1", It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private CapitationDisbursementService Service(bool withDirectory = true) => new(
        _disbursementRepo.Object,
        _statementRepo.Object,
        _runRepo.Object,
        _nachaService.Object,
        new Mock<IStripeConnectService>().Object,
        _httpClientFactory.Object,
        Configuration(),
        TestSeparationOfDuties.Create(runs: _runRepo.Object, tenantId: Tenant),
        new FactoryBackedProviderBankAccountSource(_httpClientFactory.Object),
        _dispatcher,
        new Mock<ILogger<CapitationDisbursementService>>().Object,
        httpContextAccessor: null,
        carriers: withDirectory ? _carriers : null);

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Nacha:ImmediateDestination"] = "091000019",
            ["Nacha:ImmediateOrigin"] = "1234567890",
            ["Nacha:ImmediateDestinationName"] = "TEST BANK",
            ["Nacha:ImmediateOriginName"] = "HEALTH PLAN",
            ["Nacha:CompanyName"] = "HEALTH PLAN",
            ["Nacha:CompanyId"] = "1234567890",
            ["Nacha:OriginatingDfi"] = "9100001",
        })
        .Build();
}
