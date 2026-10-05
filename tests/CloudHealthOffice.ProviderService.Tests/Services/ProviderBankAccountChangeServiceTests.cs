using System.Text.Json;
using CloudHealthOffice.ProviderService.Tests.Fakes;
using CloudHealthOffice.ProviderService.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using ProviderService.Models;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Services;

/// <summary>
/// Bank-account dual control: a change is pending until a different user
/// approves it, approval switches the active account in one conditional
/// write, and no account number reaches a log.
/// </summary>
public class ProviderBankAccountChangeServiceTests
{
    private const string Tenant = "tenant-a";
    private const string Requester = "user-requester";
    private const string Approver = "user-approver";
    private const string OldRouting = "091000019";
    private const string OldAccount = "111122223333";
    private const string NewRouting = "021000089";
    private const string NewAccount = "999988887777";

    private readonly InMemoryProviderBankAccountRepository _repo = new();
    private readonly ListLogger<ProviderBankAccountChangeService> _log = new();
    private readonly ProviderBankAccountChangeService _service;

    public ProviderBankAccountChangeServiceTests()
    {
        _service = new ProviderBankAccountChangeService(_repo, _log);
    }

    private static Provider ProviderWith(ProviderBankAccount? legacyAccount) => new()
    {
        Id = "p-1", ProviderId = "p-1", TenantId = Tenant, NPI = "1234567890",
        VersionId = "v-1", VersionState = ProviderVersionState.Active, BankAccount = legacyAccount,
    };

    private static ProviderBankAccount Account(string routing, string account) => new()
    {
        EftEnabled = true,
        PreferredDisbursementMethod = DisbursementMethod.NachaCredit,
        RoutingNumber = routing,
        AccountNumber = account,
        AccountType = BankAccountType.Checking,
        AccountHolderName = "Sunrise Clinic",
        TaxId = "12-3456789",
        TaxIdType = TaxIdType.EIN,
        W9OnFile = true,
    };

    private static BankAccountActor User(string id) => new(id, IsService: false);

    /// <summary>
    /// A provider whose old account was approved through dual control (proposed
    /// by one user, approved by another). The provider row carries the same
    /// account, as rows written before dual control do; that copy is not what
    /// makes it active.
    /// </summary>
    private async Task<Provider> ProviderWithApprovedOldAccountAsync()
    {
        var provider = ProviderWith(Account(OldRouting, OldAccount));
        await ApprovedBankAccounts.SeedAsync(_repo, provider, Account(OldRouting, OldAccount));
        return provider;
    }

    /// <summary>A record as an earlier build seeded it from the provider row: active, but never approved.</summary>
    private async Task SeedLegacyRecordAsync(Provider provider)
    {
        var record = new ProviderBankAccountRecord
        {
            TenantId = provider.TenantId, ProviderId = provider.ProviderId, ProviderNpi = provider.NPI,
            Active = Account(OldRouting, OldAccount),
            ActiveChangeId = ProviderBankAccountRecord.LegacyChangeId,
        };
        (await _repo.SaveAsync(record, 0)).Should().BeTrue();
    }

    [Fact]
    public async Task Propose_creates_a_pending_change_and_leaves_the_active_account()
    {
        var provider = await ProviderWithApprovedOldAccountAsync();

        var change = await _service.ProposeAsync(provider, Account(NewRouting, NewAccount), Requester, "test");

        change.Status.Should().Be(BankAccountChangeStatus.Pending);
        change.RequestedBy.Should().Be(Requester);
        change.TenantId.Should().Be(Tenant);
        change.ProviderId.Should().Be("p-1");
        change.Proposed!.AccountNumberLast4.Should().Be("7777");

        var active = await _service.GetActiveAccountAsync(provider);
        active!.AccountNumber.Should().Be(OldAccount, "a proposal must not change the account payments use");
        (await _service.GetPendingAsync(provider))!.Id.Should().Be(change.Id);
    }

    [Fact]
    public async Task A_new_proposal_cancels_the_previous_pending_one()
    {
        var provider = ProviderWith(null);
        var first = await _service.ProposeAsync(provider, Account(OldRouting, OldAccount), Requester, "test");

        var second = await _service.ProposeAsync(provider, Account(NewRouting, NewAccount), "user-other", "test");

        var changes = await _service.ListChangesAsync(provider);
        changes.Should().HaveCount(2);
        changes.Single(c => c.Id == first.Id).Status.Should().Be(BankAccountChangeStatus.Cancelled);
        changes.Single(c => c.Id == first.Id).DecidedBy.Should().Be("user-other");
        changes.Single(c => c.Id == first.Id).Proposed!.AccountNumber.Should().BeNull("a decided change keeps only the masked view");
        (await _service.GetPendingAsync(provider))!.Id.Should().Be(second.Id);
    }

    [Fact]
    public async Task The_requester_cannot_approve_their_own_change()
    {
        var provider = await ProviderWithApprovedOldAccountAsync();
        var change = await _service.ProposeAsync(provider, Account(NewRouting, NewAccount), Requester, "test");

        var act = () => _service.ApproveAsync(provider, change.Id, User(Requester), null);

        (await act.Should().ThrowAsync<BankAccountChangeForbiddenException>())
            .Which.Title.Should().Be("Separation of duties");
        (await _service.GetActiveAccountAsync(provider))!.AccountNumber.Should().Be(OldAccount);
        (await _service.GetPendingAsync(provider))!.Status.Should().Be(BankAccountChangeStatus.Pending);
    }

    [Fact]
    public async Task The_requester_check_ignores_case()
    {
        var provider = ProviderWith(null);
        var change = await _service.ProposeAsync(provider, Account(NewRouting, NewAccount), Requester, "test");

        var act = () => _service.ApproveAsync(provider, change.Id, User(Requester.ToUpperInvariant()), null);

        await act.Should().ThrowAsync<BankAccountChangeForbiddenException>();
    }

    [Fact]
    public async Task A_service_token_cannot_approve()
    {
        var provider = ProviderWith(null);
        var change = await _service.ProposeAsync(provider, Account(NewRouting, NewAccount), Requester, "test");

        var act = () => _service.ApproveAsync(provider, change.Id, new BankAccountActor("capitation-service", IsService: true), null);

        await act.Should().ThrowAsync<BankAccountChangeForbiddenException>();
        (await _service.GetActiveAccountAsync(provider)).Should().BeNull();
    }

    [Fact]
    public async Task A_different_user_approves_and_the_active_account_changes_with_history_kept()
    {
        var provider = await ProviderWithApprovedOldAccountAsync();
        var change = await _service.ProposeAsync(provider, Account(NewRouting, NewAccount), Requester, "test");

        var approved = await _service.ApproveAsync(provider, change.Id, User(Approver), "verified by phone");

        approved.Status.Should().Be(BankAccountChangeStatus.Approved);
        approved.DecidedBy.Should().Be(Approver);
        approved.Reason.Should().Be("verified by phone");

        var active = await _service.GetActiveAccountAsync(provider);
        active!.AccountNumber.Should().Be(NewAccount);
        active.RoutingNumber.Should().Be(NewRouting);

        var history = (await _service.ListChangesAsync(provider)).Single(c => c.Id == change.Id);
        history.PreviousAccount!.AccountNumberLast4.Should().Be("3333");
        history.PreviousAccount.AccountNumber.Should().BeNull("history is masked");
        history.PreviousAccount.RoutingNumber.Should().BeNull();
        history.Proposed!.AccountNumber.Should().BeNull();
        (await _service.GetPendingAsync(provider)).Should().BeNull();

        var record = _repo.Records.Single();
        record.ActiveChangeId.Should().Be(change.Id);
        record.ActiveApprovedBy.Should().Be(Approver);
    }

    [Fact]
    public async Task Approving_a_superseded_change_is_a_conflict()
    {
        var provider = ProviderWith(null);
        var stale = await _service.ProposeAsync(provider, Account(OldRouting, OldAccount), Requester, "test");
        await _service.ProposeAsync(provider, Account(NewRouting, NewAccount), Requester, "test");

        var act = () => _service.ApproveAsync(provider, stale.Id, User(Approver), null);

        await act.Should().ThrowAsync<BankAccountChangeConflictException>();
        (await _service.GetActiveAccountAsync(provider)).Should().BeNull();
    }

    [Fact]
    public async Task Approving_an_already_approved_change_is_a_conflict()
    {
        var provider = ProviderWith(null);
        var change = await _service.ProposeAsync(provider, Account(NewRouting, NewAccount), Requester, "test");
        await _service.ApproveAsync(provider, change.Id, User(Approver), null);

        var act = () => _service.ApproveAsync(provider, change.Id, User("user-third"), null);

        await act.Should().ThrowAsync<BankAccountChangeConflictException>();
    }

    [Fact]
    public async Task Approving_when_the_active_account_changed_since_the_request_is_a_conflict()
    {
        var provider = await ProviderWithApprovedOldAccountAsync();
        var change = await _service.ProposeAsync(provider, Account(NewRouting, NewAccount), Requester, "test");

        // The record's active account moved on after the change was proposed
        // (written out of band, e.g. a restored backup).
        var record = _repo.Records.Single();
        record.ActiveChangeId = "some-other-change";
        (await _repo.SaveAsync(record, record.Revision)).Should().BeTrue();

        var act = () => _service.ApproveAsync(provider, change.Id, User(Approver), null);

        await act.Should().ThrowAsync<BankAccountChangeConflictException>()
            .WithMessage("*has changed since*");
        _repo.Records.Single().Active!.AccountNumber.Should().Be(OldAccount);
    }

    [Fact]
    public async Task Approval_that_loses_a_race_with_another_write_is_a_conflict_and_changes_nothing()
    {
        var provider = await ProviderWithApprovedOldAccountAsync();
        var change = await _service.ProposeAsync(provider, Account(NewRouting, NewAccount), Requester, "test");
        _repo.AfterNextGet = repo => repo.TouchAll();

        var act = () => _service.ApproveAsync(provider, change.Id, User(Approver), null);

        await act.Should().ThrowAsync<BankAccountChangeConflictException>();
        var stored = _repo.Records.Single();
        stored.Active!.AccountNumber.Should().Be(OldAccount);
        stored.Pending!.Id.Should().Be(change.Id);
    }

    [Fact]
    public async Task Reject_leaves_the_active_account_and_drops_the_proposed_numbers()
    {
        var provider = await ProviderWithApprovedOldAccountAsync();
        var change = await _service.ProposeAsync(provider, Account(NewRouting, NewAccount), Requester, "test");

        var rejected = await _service.RejectAsync(provider, change.Id, User(Approver), "caller could not be verified");

        rejected.Status.Should().Be(BankAccountChangeStatus.Rejected);
        rejected.DecidedBy.Should().Be(Approver);
        rejected.Reason.Should().Be("caller could not be verified");
        (await _service.GetActiveAccountAsync(provider))!.AccountNumber.Should().Be(OldAccount);
        (await _service.GetPendingAsync(provider)).Should().BeNull();
        JsonSerializer.Serialize(_repo.Records).Should().NotContain(NewAccount);
    }

    [Fact]
    public async Task Cancel_withdraws_a_pending_change()
    {
        var provider = ProviderWith(null);
        var change = await _service.ProposeAsync(provider, Account(NewRouting, NewAccount), Requester, "test");

        var cancelled = await _service.CancelAsync(provider, change.Id, Requester, null);

        cancelled.Status.Should().Be(BankAccountChangeStatus.Cancelled);
        (await _service.GetPendingAsync(provider)).Should().BeNull();
        var act = () => _service.ApproveAsync(provider, change.Id, User(Approver), null);
        await act.Should().ThrowAsync<BankAccountChangeConflictException>();
    }

    [Fact]
    public async Task First_account_is_pending_and_payments_have_no_account_until_approved()
    {
        var provider = ProviderWith(null);
        var change = await _service.ProposeAsync(provider, Account(NewRouting, NewAccount), Requester, "POST providers");

        (await _service.GetActiveAccountAsync(provider)).Should().BeNull();

        await _service.ApproveAsync(provider, change.Id, User(Approver), null);

        (await _service.GetActiveAccountAsync(provider))!.AccountNumber.Should().Be(NewAccount);
        (await _service.ListChangesAsync(provider)).Single().PreviousAccount.Should().BeNull();
    }

    [Fact]
    public async Task An_account_on_the_provider_row_from_before_dual_control_is_not_active()
    {
        var provider = ProviderWith(Account(OldRouting, OldAccount));

        (await _service.GetActiveAccountAsync(provider)).Should().BeNull(
            "an account set before dual control must be proposed and approved once");
        (await _service.DiffersFromCurrentAsync(provider, Account(OldRouting, OldAccount))).Should().BeTrue(
            "sending the row's account again is a proposal, not an echo");
    }

    [Fact]
    public async Task A_record_seeded_from_the_provider_row_is_not_active()
    {
        var provider = ProviderWith(Account(OldRouting, OldAccount));
        await SeedLegacyRecordAsync(provider);

        (await _service.GetActiveAccountAsync(provider)).Should().BeNull();
        (await _service.DiffersFromCurrentAsync(provider, Account(OldRouting, OldAccount))).Should().BeTrue();
        (await _service.DiffersFromCurrentAsync(provider, BankAccountMasking.Mask(Account(OldRouting, OldAccount))!))
            .Should().BeTrue("a masked copy of an unapproved account is not an echo of the active one");
    }

    [Fact]
    public async Task An_active_account_whose_change_id_names_no_approved_change_is_not_active()
    {
        var provider = ProviderWith(null);
        var record = new ProviderBankAccountRecord
        {
            TenantId = Tenant, ProviderId = "p-1", ProviderNpi = provider.NPI,
            Active = Account(OldRouting, OldAccount), ActiveChangeId = "change-never-approved",
        };
        (await _repo.SaveAsync(record, 0)).Should().BeTrue();

        (await _service.GetActiveAccountAsync(provider)).Should().BeNull();
    }

    [Fact]
    public async Task A_legacy_seeded_account_becomes_active_only_once_proposed_and_approved_by_another_user()
    {
        var provider = ProviderWith(Account(OldRouting, OldAccount));
        await SeedLegacyRecordAsync(provider);

        var change = await _service.ProposeAsync(provider, Account(OldRouting, OldAccount), Requester, "test");
        (await _service.GetActiveAccountAsync(provider)).Should().BeNull("a proposal is not an approval");

        await _service.ApproveAsync(provider, change.Id, User(Approver), null);

        (await _service.GetActiveAccountAsync(provider))!.AccountNumber.Should().Be(OldAccount);
        (await _service.ListChangesAsync(provider)).Single().PreviousAccount.Should().BeNull(
            "the seeded account was never active, so it is not the previous one");
    }

    [Fact]
    public async Task Echoing_the_active_or_pending_account_is_not_a_change()
    {
        var provider = await ProviderWithApprovedOldAccountAsync();
        (await _service.DiffersFromCurrentAsync(provider, Account(OldRouting, OldAccount))).Should().BeFalse();

        await _service.ProposeAsync(provider, Account(NewRouting, NewAccount), Requester, "test");
        (await _service.DiffersFromCurrentAsync(provider, Account(NewRouting, NewAccount))).Should().BeFalse();
        (await _service.DiffersFromCurrentAsync(provider, Account(NewRouting, "555566667777"))).Should().BeTrue();
    }

    [Fact]
    public async Task Audit_entries_name_actor_provider_and_tenant_and_never_an_account_number()
    {
        var provider = await ProviderWithApprovedOldAccountAsync();
        var first = await _service.ProposeAsync(provider, Account(NewRouting, NewAccount), Requester, "test");
        var second = await _service.ProposeAsync(provider, Account(NewRouting, "555566667777"), Requester, "test");
        try { await _service.ApproveAsync(provider, second.Id, User(Requester), null); } catch (BankAccountChangeForbiddenException) { }
        await _service.ApproveAsync(provider, second.Id, User(Approver), null);
        var third = await _service.ProposeAsync(provider, Account(OldRouting, OldAccount), Requester, "test");
        await _service.RejectAsync(provider, third.Id, User(Approver), null);

        var messages = _log.Entries.Select(e => e.Message).ToList();
        messages.Should().Contain(m => m.Contains("proposed") && m.Contains(first.Id) && m.Contains(Requester) && m.Contains(Tenant) && m.Contains("p-1"));
        messages.Should().Contain(m => m.Contains("cancelled") && m.Contains(first.Id));
        messages.Should().Contain(m => m.Contains("refused") && m.Contains(second.Id) && m.Contains(Requester));
        messages.Should().Contain(m => m.Contains("approved") && m.Contains(second.Id) && m.Contains(Approver) && m.Contains(Tenant));
        messages.Should().Contain(m => m.Contains("rejected") && m.Contains(third.Id));

        foreach (var number in new[] { OldRouting, OldAccount, NewRouting, NewAccount, "555566667777", "12-3456789" })
        {
            messages.Should().NotContain(m => m.Contains(number), $"no log entry may carry {number}");
        }
    }
}
