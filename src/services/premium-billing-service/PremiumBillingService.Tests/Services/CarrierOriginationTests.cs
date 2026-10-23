using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.NachaTransmission;
using CloudHealthOffice.PlanConfiguration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PremiumBillingService.Clients;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;
using PremiumBillingService.Services;

namespace PremiumBillingService.Tests.Services;

/// <summary>
/// Which legal entity the sponsor debits are originated by
/// (plan-configuration.md §12.3).
///
/// <para>
/// Before carriers existed this came from the deployment's <c>Nacha:*</c>
/// configuration, one set of values for every tenant in the deployment. The
/// carrier is now asked first, and the configured values stand wherever it
/// states nothing — so a deployment that has not filled a carrier in keeps
/// originating exactly as it did.
/// </para>
///
/// <para>
/// The case worth having tests for is the third one: tenant-service
/// unreachable. The bank is being told which entity is drawing on a sponsor's
/// account, and from here a carrier that states nothing looks the same as a
/// carrier that could not be read. Only the first is safe to originate under
/// the configured entity, so the release fails and the drafts go back to
/// Pending rather than money moving in the wrong name.
/// </para>
/// </summary>
public class CarrierOriginationTests
{
    private const string Tenant = "tenant-1";

    private readonly Mock<IEftDraftRepository> _draftRepo = new();
    private readonly Mock<IPremiumInvoiceRepository> _invoiceRepo = new();
    private readonly Mock<IBillingRunRepository> _billingRunRepo = new();
    private readonly Mock<INachaFileService> _nachaService = new();
    private readonly Mock<ISponsorBankAccountSource> _bankAccounts = new();
    private readonly RecordingNachaDispatcher _dispatcher = new();
    private readonly InMemoryCarrierDirectory _carriers = new();
    private readonly List<NachaFileOptions> _built = [];

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
        _draftRepo.Setup(r => r.TryClaimForReleaseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        _draftRepo.Setup(r => r.UpdateAsync(It.IsAny<EftDraft>())).ReturnsAsync((EftDraft d) => d);
        _draftRepo.Setup(r => r.ReleaseClaimAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);
        _draftRepo.Setup(r => r.GetByStatusAsync(EftDraftStatus.Pending)).ReturnsAsync(
        [
            new EftDraft
            {
                Id = "draft-1", TenantId = Tenant, InvoiceId = "inv-1", GroupNumber = "ACME",
                Amount = 1500m, Method = EftMethod.Nacha, Status = EftDraftStatus.Pending,
            },
        ]);

        _bankAccounts
            .Setup(b => b.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SponsorBankAccountLookup.Found(new SponsorBankAccount
            {
                EftEnabled = true, RoutingNumber = "091000019", AccountNumber = "123456789",
                RoutingNumberLast4 = "0019", AccountNumberLast4 = "6789", AccountHolderName = "ACME CORP",
            }));

        _nachaService
            .Setup(s => s.GenerateNachaFile(It.IsAny<List<NachaEntryDetail>>(), It.IsAny<NachaFileOptions>()))
            .Returns((List<NachaEntryDetail> _, NachaFileOptions options) =>
            {
                _built.Add(options);
                return new GeneratedNachaFile { FileReference = "NACHA-TEST", FileContent = "1", FileName = "f.ach" };
            });
    }

    [Fact]
    public async Task A_carrier_that_states_an_origination_is_what_the_file_is_originated_under()
    {
        _carriers.Add(Tenant, FilledIn);

        await Service().GenerateNachaFileForPendingDraftsAsync();

        var options = _built.Should().ContainSingle().Subject;
        options.ImmediateOrigin.Should().Be("1742938475");
        options.CompanyId.Should().Be("1742938475");
        options.CompanyName.Should().Be("ACME HEALTH PLAN OF TEXAS INC");
        options.ImmediateOriginName.Should().Be("ACME HEALTH PLAN OF TEXAS INC");
        // Never the carrier's: the bank the file goes to is the deployment's.
        options.ImmediateDestination.Should().Be("091000019");
    }

    [Fact]
    public async Task A_carrier_that_states_nothing_leaves_the_configured_origination_in_place()
    {
        // What every deployment looks like the day step 2b ships: the default
        // carrier exists because a tenant must have one, and nobody has filled
        // its legal-entity details in yet.
        _carriers.Add(Tenant, new CarrierIdentity
        {
            Id = $"{Tenant}|DEFAULT", Code = "DEFAULT", Name = "Acme Health", IsDefault = true,
        });

        await Service().GenerateNachaFileForPendingDraftsAsync();

        var options = _built.Should().ContainSingle().Subject;
        options.ImmediateOrigin.Should().Be("1234567890");
        options.CompanyId.Should().Be("1234567890");
        options.CompanyName.Should().Be("HEALTH PLAN", "the carrier's staff name is not letterhead");
    }

    [Fact]
    public async Task A_correspondence_name_is_preferred_to_the_legal_name()
    {
        _carriers.Add(Tenant, FilledIn with { CorrespondenceName = "Acme Health Plan" });

        await Service().GenerateNachaFileForPendingDraftsAsync();

        _built.Should().ContainSingle().Which.CompanyName.Should().Be("Acme Health Plan");
    }

    [Fact]
    public async Task A_host_with_no_carrier_directory_originates_exactly_as_before()
    {
        await Service(withDirectory: false).GenerateNachaFileForPendingDraftsAsync();

        var options = _built.Should().ContainSingle().Subject;
        options.ImmediateOrigin.Should().Be("1234567890");
        options.CompanyName.Should().Be("HEALTH PLAN");
        _carriers.Calls.Should().Be(0);
    }

    [Fact]
    public async Task An_unreachable_tenant_service_sends_nothing_and_leaves_the_drafts_pending()
    {
        _carriers.Unavailable = true;

        var release = () => Service().GenerateNachaFileForPendingDraftsAsync();

        await release.Should().ThrowAsync<HttpRequestException>();
        _built.Should().BeEmpty("no file is built when the originating entity is not known");
        _dispatcher.Sent.Should().BeEmpty();
        _draftRepo.Verify(r => r.ReleaseClaimAsync("draft-1", It.IsAny<string>(), It.IsAny<string?>()), Times.Once);
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private EftDraftService Service(bool withDirectory = true)
    {
        var actor = new Mock<ICurrentActor>();
        actor.SetupGet(a => a.UserId).Returns("approver-1");
        actor.SetupGet(a => a.IsAuthenticated).Returns(true);
        actor.SetupGet(a => a.IsService).Returns(false);
        actor.SetupGet(a => a.TenantId).Returns(Tenant);

        return new EftDraftService(
            _draftRepo.Object, _invoiceRepo.Object, _billingRunRepo.Object, _nachaService.Object,
            new Mock<IStripeAchService>().Object, _bankAccounts.Object, _dispatcher, actor.Object,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            Configuration(), new Mock<ILogger<EftDraftService>>().Object,
            withDirectory ? _carriers : null);
    }

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
