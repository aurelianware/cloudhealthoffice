using System.Collections.Concurrent;
using IdCardService.Models;

namespace IdCardService.Repositories;

/// <summary>
/// In-memory implementations used for dev, unit tests, and integration tests
/// that don't need a real database.
/// </summary>
public class InMemoryIdCardOrderRepository : IIdCardOrderRepository
{
    private readonly ConcurrentDictionary<(string, string), IdCardOrder> _store = new();

    public Task UpsertAsync(IdCardOrder order, CancellationToken ct = default)
    {
        _store[(order.TenantId, order.Id)] = order;
        return Task.CompletedTask;
    }

    public Task<IdCardOrder?> GetAsync(string tenantId, string orderId, CancellationToken ct = default)
    {
        _store.TryGetValue((tenantId, orderId), out var order);
        return Task.FromResult(order);
    }
}

public class InMemoryIdCardRecordRepository : IIdCardRecordRepository
{
    private readonly ConcurrentDictionary<(string, string), IdCardRecord> _byCardId = new();

    public Task UpsertAsync(IdCardRecord record, CancellationToken ct = default)
    {
        _byCardId[(record.TenantId, record.CardId)] = record;
        return Task.CompletedTask;
    }

    public Task<IdCardRecord?> FindByCardIdAsync(string tenantId, string cardId, CancellationToken ct = default)
    {
        _byCardId.TryGetValue((tenantId, cardId), out var r);
        return Task.FromResult(r);
    }

    public Task<List<IdCardRecord>> ListForMemberAsync(string tenantId, string memberId, CancellationToken ct = default)
    {
        var list = _byCardId.Values
            .Where(r => r.TenantId == tenantId && r.MemberId == memberId)
            .OrderByDescending(r => r.IssuedAt)
            .ToList();
        return Task.FromResult(list);
    }

    public Task<List<IdCardRecord>> ListIssuedSinceAsync(DateTime since, CancellationToken ct = default)
    {
        var list = _byCardId.Values.Where(r => r.IssuedAt >= since).ToList();
        return Task.FromResult(list);
    }
}

public class InMemoryIdCardTemplateRepository : IIdCardTemplateRepository
{
    private readonly ConcurrentDictionary<string, IdCardTemplate> _byId = new();

    public Task UpsertAsync(IdCardTemplate template, CancellationToken ct = default)
    {
        _byId[template.Id] = template;
        return Task.CompletedTask;
    }

    public Task<IdCardTemplate?> FindBySponsorAndPlanAsync(string tenantId, string sponsorId, string planId, CancellationToken ct = default)
    {
        var t = _byId.Values.FirstOrDefault(x =>
            x.TenantId == tenantId && x.SponsorId == sponsorId && x.PlanId == planId);
        return Task.FromResult(t);
    }

    public Task<IdCardTemplate?> FindSponsorDefaultAsync(string tenantId, string sponsorId, CancellationToken ct = default)
    {
        var t = _byId.Values.FirstOrDefault(x =>
            x.TenantId == tenantId && x.SponsorId == sponsorId && x.PlanId == null);
        return Task.FromResult(t);
    }

    public Task<IdCardTemplate?> FindGlobalDefaultAsync(string tenantId, CancellationToken ct = default)
    {
        var t = _byId.Values.FirstOrDefault(x =>
            x.TenantId == tenantId && x.IsGlobalDefault);
        return Task.FromResult(t);
    }
}
