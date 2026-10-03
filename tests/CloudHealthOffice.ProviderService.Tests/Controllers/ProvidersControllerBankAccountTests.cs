using CloudHealthOffice.ProviderService.Tests.Fakes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProviderService.Controllers;
using ProviderService.Models;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Controllers;

/// <summary>
/// Provider writes never set a bank account: an account in a create or update
/// body becomes a pending change, and a new provider version keeps the
/// account the chain head already had. In-memory stores, no Mongo.
/// </summary>
public class ProvidersControllerBankAccountTests
{
    private const string TenantId = "tenant-a";
    private const string ProviderId = "p-001";
    private const string OldAccount = "111122223333";
    private const string NewAccount = "999988887777";

    private readonly InMemoryProviderRepository _providers = new() { TenantId = TenantId };
    private readonly InMemoryProviderBankAccountRepository _bankAccounts = new();
    private readonly ProviderVersioningService _versioning;
    private readonly ProviderBankAccountChangeService _changes;
    private readonly ProvidersController _controller;

    public ProvidersControllerBankAccountTests()
    {
        _versioning = new ProviderVersioningService(
            _providers, new InMemoryProviderTransitionRepository(), new FakeProviderVersionEventPublisher(),
            NullLogger<ProviderVersioningService>.Instance);
        _changes = new ProviderBankAccountChangeService(_bankAccounts, NullLogger<ProviderBankAccountChangeService>.Instance);
        _controller = new ProvidersController(
            providerRepository: _providers,
            versioning: _versioning,
            adapterFactory: null!,
            integrityProjection: null!,
            panelGatingValidator: new PanelGatingValidator(
                NullLogger<PanelGatingValidator>.Instance, new StaticOptions()),
            credentialing: null!,
            bankAccountChanges: _changes,
            logger: NullLogger<ProvidersController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = TestHelpers.TokenHttpContext.For(TenantId, "user-editor") },
        };

        _providers.CreateAsync(new Provider
        {
            Id = ProviderId, ProviderId = ProviderId, TenantId = TenantId, NPI = "1234567890",
            ProviderType = ProviderType.Individual, FirstName = "Test", LastName = "Provider",
            VersionId = ProviderId, VersionNumber = 1, VersionState = ProviderVersionState.Active,
            BankAccount = Account(OldAccount),
        }).GetAwaiter().GetResult();
    }

    private static ProviderBankAccount Account(string accountNumber) => new()
    {
        EftEnabled = true,
        PreferredDisbursementMethod = DisbursementMethod.NachaCredit,
        RoutingNumber = "091000019",
        AccountNumber = accountNumber,
        AccountHolderName = "Test Provider",
    };

    private async Task<Provider> Head() => (await _providers.GetByIdAsync(ProviderId))!;

    private static Provider Body(Provider from, ProviderBankAccount? account)
    {
        var body = System.Text.Json.JsonSerializer.Deserialize<Provider>(System.Text.Json.JsonSerializer.Serialize(from))!;
        body.BankAccount = account;
        return body;
    }

    [Fact]
    public async Task Update_with_a_new_account_keeps_the_active_account_and_creates_a_pending_change()
    {
        var head = await Head();

        var result = await _controller.UpdateProvider(ProviderId, Body(head, Account(NewAccount)));

        result.Result.Should().BeOfType<OkObjectResult>();
        var updated = await Head();
        updated.VersionNumber.Should().Be(2);
        updated.BankAccount!.AccountNumber.Should().Be(OldAccount, "the new version must not carry the body's account");
        (await _changes.GetActiveAccountAsync(updated))!.AccountNumber.Should().Be(OldAccount);
        var pending = await _changes.GetPendingAsync(updated);
        pending!.RequestedBy.Should().Be("user-editor");
        pending.Proposed!.AccountNumber.Should().Be(NewAccount);
    }

    [Fact]
    public async Task Update_echoing_the_current_account_creates_no_change()
    {
        var head = await Head();

        await _controller.UpdateProvider(ProviderId, Body(head, Account(OldAccount)));

        _bankAccounts.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_echoing_the_masked_account_a_read_returned_creates_no_change()
    {
        // GET responses mask the account; a client that PUTs the GET body back
        // (mcc-platform-validator does) sends the masked copy.
        var head = await Head();

        await _controller.UpdateProvider(ProviderId, Body(head, BankAccountMasking.Mask(head.BankAccount)));

        _bankAccounts.Records.Should().BeEmpty();
        (await Head()).BankAccount!.AccountNumber.Should().Be(OldAccount);
    }

    [Fact]
    public async Task Update_without_an_account_keeps_the_account()
    {
        var head = await Head();

        await _controller.UpdateProvider(ProviderId, Body(head, null));

        (await Head()).BankAccount!.AccountNumber.Should().Be(OldAccount);
        _bankAccounts.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Activating_a_version_keeps_the_head_account_whatever_the_draft_carries()
    {
        var draft = await _versioning.AmendActiveProviderAsync(ProviderId, "user-editor");
        draft.BankAccount = Account(NewAccount);
        await _providers.UpdateDraftAsync(draft);

        var activated = await _versioning.ActivateVersionAsync(ProviderId, draft.VersionId, "user-editor");

        activated.BankAccount!.AccountNumber.Should().Be(OldAccount);
        (await Head()).BankAccount!.AccountNumber.Should().Be(OldAccount);
    }

    [Fact]
    public async Task A_new_provider_draft_never_carries_an_account()
    {
        var result = await _controller.CreateDraft(new Provider
        {
            NPI = "2222222222", ProviderType = ProviderType.Individual, FirstName = "New", LastName = "Provider",
            BankAccount = Account(NewAccount),
        });

        var draft = (Provider)((CreatedAtActionResult)result.Result!).Value!;
        draft.BankAccount.Should().BeNull();
        var stored = await _providers.GetVersionAsync(draft.ProviderId, draft.VersionId);
        stored!.BankAccount.Should().BeNull();
        var pending = await _changes.GetPendingAsync(stored);
        pending!.Proposed!.AccountNumber.Should().Be(NewAccount);
        (await _changes.GetActiveAccountAsync(stored)).Should().BeNull();
    }

    private sealed class StaticOptions : IOptionsMonitor<NetworkParticipationBackfillOptions>
    {
        public NetworkParticipationBackfillOptions CurrentValue { get; } = new();
        public NetworkParticipationBackfillOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<NetworkParticipationBackfillOptions, string?> listener) => null;
    }
}
