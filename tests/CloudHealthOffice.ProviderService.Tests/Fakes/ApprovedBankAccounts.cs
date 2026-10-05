using Microsoft.Extensions.Logging.Abstractions;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Fakes;

/// <summary>
/// Gives a provider an approved bank account the only way production does:
/// one user proposes it and a different user approves it, through the real
/// <see cref="ProviderBankAccountChangeService"/>. An account on the provider
/// row, or a record seeded from it, is not approved.
/// </summary>
public static class ApprovedBankAccounts
{
    public const string SeedRequester = "user-seed-requester";
    public const string SeedApprover = "user-seed-approver";

    public static async Task<PendingBankAccountChange> SeedAsync(
        IProviderBankAccountRepository repository, Provider provider, ProviderBankAccount account,
        string requester = SeedRequester, string approver = SeedApprover)
    {
        var service = new ProviderBankAccountChangeService(repository, NullLogger<ProviderBankAccountChangeService>.Instance);
        var change = await service.ProposeAsync(provider, account, requester, "test seed");
        return await service.ApproveAsync(provider, change.Id, new BankAccountActor(approver, IsService: false), "test seed");
    }
}
