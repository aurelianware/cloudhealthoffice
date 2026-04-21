using IdCardService.Models;

namespace IdCardService.Repositories;

public interface IIdCardOrderRepository
{
    Task UpsertAsync(IdCardOrder order, CancellationToken ct = default);
    Task<IdCardOrder?> GetAsync(string tenantId, string orderId, CancellationToken ct = default);
}

public interface IIdCardRecordRepository
{
    Task UpsertAsync(IdCardRecord record, CancellationToken ct = default);
    Task<IdCardRecord?> FindByCardIdAsync(string tenantId, string cardId, CancellationToken ct = default);
    Task<List<IdCardRecord>> ListForMemberAsync(string tenantId, string memberId, CancellationToken ct = default);
    Task<List<IdCardRecord>> ListIssuedSinceAsync(DateTime since, CancellationToken ct = default);
}

public interface IIdCardTemplateRepository
{
    Task UpsertAsync(IdCardTemplate template, CancellationToken ct = default);
    Task<IdCardTemplate?> FindBySponsorAndPlanAsync(string tenantId, string sponsorId, string planId, CancellationToken ct = default);
    Task<IdCardTemplate?> FindSponsorDefaultAsync(string tenantId, string sponsorId, CancellationToken ct = default);
    Task<IdCardTemplate?> FindGlobalDefaultAsync(string tenantId, CancellationToken ct = default);
}
