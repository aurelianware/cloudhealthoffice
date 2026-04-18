using AccumulatorService.Models;
using Microsoft.Azure.Cosmos;

namespace AccumulatorService.Repositories;

public class AccumulatorRepositoryCosmos : IAccumulatorRepository
{
    private readonly Container _snapshots;
    private readonly Container _events;

    public AccumulatorRepositoryCosmos(CosmosClient client)
    {
        var db = client.GetDatabase("CloudHealthOffice");
        _snapshots = db.GetContainer("AccumulatorSnapshots");
        _events = db.GetContainer("AccumulatorEvents");
    }

    public async Task<AccumulatorSnapshot?> GetSnapshotAsync(string tenantId, string memberId, DateTime planYearStart, CancellationToken ct = default)
    {
        var id = AccumulatorSnapshot.BuildId(tenantId, memberId, planYearStart);
        try
        {
            var resp = await _snapshots.ReadItemAsync<AccumulatorSnapshot>(id, new PartitionKey(tenantId), cancellationToken: ct);
            return resp.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<AccumulatorSnapshot?> GetSnapshotByAsOfDateAsync(string tenantId, string memberId, DateTime asOfDate, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
                @"SELECT * FROM c WHERE c.tenantId = @tenantId AND c.memberId = @memberId
                  AND c.planYearStart <= @asOf AND c.planYearEnd >= @asOf")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@memberId", memberId)
            .WithParameter("@asOf", asOfDate);

        using var iter = _snapshots.GetItemQueryIterator<AccumulatorSnapshot>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        if (iter.HasMoreResults)
        {
            var page = await iter.ReadNextAsync(ct);
            return page.FirstOrDefault();
        }
        return null;
    }

    public async Task<IReadOnlyList<AccumulatorSnapshot>> GetSnapshotsAsync(string tenantId, string memberId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.memberId = @memberId ORDER BY c.planYearStart DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@memberId", memberId);
        var results = new List<AccumulatorSnapshot>();
        using var iter = _snapshots.GetItemQueryIterator<AccumulatorSnapshot>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        while (iter.HasMoreResults)
        {
            var page = await iter.ReadNextAsync(ct);
            results.AddRange(page);
        }
        return results;
    }

    public async Task UpsertSnapshotAsync(AccumulatorSnapshot snapshot, CancellationToken ct = default)
    {
        snapshot.LastUpdatedDate = DateTime.UtcNow;
        await _snapshots.UpsertItemAsync(snapshot, new PartitionKey(snapshot.TenantId), cancellationToken: ct);
    }

    public async Task AppendEventAsync(AccumulatorEvent evt, CancellationToken ct = default)
    {
        await _events.CreateItemAsync(evt, new PartitionKey(evt.TenantId), cancellationToken: ct);
    }

    public async Task<IReadOnlyList<AccumulatorEvent>> GetEventsAsync(string tenantId, string memberId, int take = 100, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
                @"SELECT TOP @take * FROM c WHERE c.tenantId = @tenantId AND c.memberId = @memberId
                  ORDER BY c.occurredAt DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@memberId", memberId)
            .WithParameter("@take", take);
        var results = new List<AccumulatorEvent>();
        using var iter = _events.GetItemQueryIterator<AccumulatorEvent>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        while (iter.HasMoreResults)
        {
            var page = await iter.ReadNextAsync(ct);
            results.AddRange(page);
        }
        return results;
    }

    public async Task<AccumulatorEvent?> GetManualAdjustmentAsync(string tenantId, string adjustmentId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
                @"SELECT TOP 1 * FROM c WHERE c.tenantId = @tenantId
                  AND c.eventType = 'ManualAdjustment' AND c.sourceReference = @adjustmentId")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@adjustmentId", adjustmentId);
        using var iter = _events.GetItemQueryIterator<AccumulatorEvent>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        if (iter.HasMoreResults)
        {
            var page = await iter.ReadNextAsync(ct);
            return page.FirstOrDefault();
        }
        return null;
    }
}

public class ProcessedClaimStoreCosmos : IProcessedClaimStore
{
    private readonly Container _col;

    public ProcessedClaimStoreCosmos(CosmosClient client)
    {
        var db = client.GetDatabase("CloudHealthOffice");
        _col = db.GetContainer("AccumulatorProcessedClaims");
    }

    public async Task<BeginClaimOutcome> TryBeginAsync(string tenantId, string claimId, CancellationToken ct = default)
    {
        var marker = new ProcessedClaim
        {
            Id = ProcessedClaim.BuildId(tenantId, claimId),
            TenantId = tenantId,
            ClaimId = claimId,
            ProcessedAt = DateTime.UtcNow,
            Outcome = "Pending"
        };
        try
        {
            await _col.CreateItemAsync(marker, new PartitionKey(tenantId), cancellationToken: ct);
            return BeginClaimOutcome.Proceed;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // See Mongo implementation: Pending marker means a prior attempt
            // crashed mid-flight and this call should retry the apply, not skip.
            var existing = await GetAsync(tenantId, claimId, ct);
            if (existing is null || string.Equals(existing.Outcome, "Pending", StringComparison.Ordinal))
            {
                return BeginClaimOutcome.Proceed;
            }
            return BeginClaimOutcome.AlreadyApplied;
        }
    }

    public async Task CompleteAsync(string tenantId, string claimId, string resultingEventId, string outcome, CancellationToken ct = default)
    {
        var id = ProcessedClaim.BuildId(tenantId, claimId);
        var existing = await GetAsync(tenantId, claimId, ct);
        if (existing is null) return;
        existing.ResultingEventId = resultingEventId;
        existing.Outcome = outcome;
        existing.ProcessedAt = DateTime.UtcNow;
        await _col.UpsertItemAsync(existing, new PartitionKey(tenantId), cancellationToken: ct);
    }

    public async Task<ProcessedClaim?> GetAsync(string tenantId, string claimId, CancellationToken ct = default)
    {
        var id = ProcessedClaim.BuildId(tenantId, claimId);
        try
        {
            var resp = await _col.ReadItemAsync<ProcessedClaim>(id, new PartitionKey(tenantId), cancellationToken: ct);
            return resp.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}
