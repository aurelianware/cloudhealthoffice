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

    public async Task<bool> TryReplaceSnapshotAsync(AccumulatorSnapshot snapshot, long expectedVersion, CancellationToken ct = default)
    {
        snapshot.LastUpdatedDate = DateTime.UtcNow;
        var pk = new PartitionKey(snapshot.TenantId);
        ItemResponse<AccumulatorSnapshot>? current = null;
        try
        {
            current = await _snapshots.ReadItemAsync<AccumulatorSnapshot>(snapshot.Id, pk, cancellationToken: ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
        }

        try
        {
            if (current is null)
            {
                if (expectedVersion != 0) return false;
                await _snapshots.CreateItemAsync(snapshot, pk, cancellationToken: ct);
                return true;
            }

            if (current.Resource.Version != expectedVersion) return false;
            // The ETag makes read-compare-replace atomic: a write in between fails.
            await _snapshots.ReplaceItemAsync(snapshot, snapshot.Id, pk,
                new ItemRequestOptions { IfMatchEtag = current.ETag }, ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Conflict
                                             or System.Net.HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }

    public async Task<bool> TryAppendEventAsync(AccumulatorEvent evt, CancellationToken ct = default)
    {
        // Event ids are one per (snapshot, version) (AccumulatorEvent.BuildId),
        // so a second writer at the same version conflicts on the id.
        try
        {
            await _events.CreateItemAsync(evt, new PartitionKey(evt.TenantId), cancellationToken: ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<AccumulatorEvent>> GetAggregateEventsAsync(
        string tenantId, string aggregateId, long afterVersion = 0, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
                @"SELECT * FROM c WHERE c.tenantId = @tenantId AND c.aggregateId = @aggregateId
                  AND c.version > @afterVersion ORDER BY c.version")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@aggregateId", aggregateId)
            .WithParameter("@afterVersion", afterVersion);
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

    public async Task<AccumulatorEvent?> GetClaimReversedEventAsync(string tenantId, string claimId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
                @"SELECT TOP 1 * FROM c WHERE c.tenantId = @tenantId
                  AND c.eventType = 'ClaimReversed' AND c.sourceClaimId = @claimId")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@claimId", claimId);
        using var iter = _events.GetItemQueryIterator<AccumulatorEvent>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        if (iter.HasMoreResults)
        {
            var page = await iter.ReadNextAsync(ct);
            return page.FirstOrDefault();
        }
        return null;
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

    public async Task<AccumulatorEvent?> GetClaimAppliedEventAsync(string tenantId, string claimId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
                @"SELECT TOP 1 * FROM c WHERE c.tenantId = @tenantId
                  AND c.eventType = 'ClaimApplied' AND c.sourceClaimId = @claimId
                  ORDER BY c.occurredAt DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@claimId", claimId);
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
    private readonly TimeSpan _lease;
    private readonly TimeProvider _clock;

    public ProcessedClaimStoreCosmos(CosmosClient client)
        : this(client, ProcessedClaimLease.Timeout, TimeProvider.System)
    {
    }

    public ProcessedClaimStoreCosmos(CosmosClient client, TimeSpan lease, TimeProvider clock)
    {
        _lease = lease;
        _clock = clock;
        var db = client.GetDatabase("CloudHealthOffice");
        _col = db.GetContainer("AccumulatorProcessedClaims");
    }

    public async Task<BeginClaimOutcome> TryBeginAsync(string tenantId, string claimId, CancellationToken ct = default) =>
        (await BeginLeaseAsync(tenantId, claimId, ct)).Outcome;

    public async Task<ClaimLease> BeginLeaseAsync(string tenantId, string claimId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var id = ProcessedClaim.BuildId(tenantId, claimId);
        var pk = new PartitionKey(tenantId);
        var token = Guid.NewGuid().ToString("N");
        var marker = new ProcessedClaim
        {
            Id = id,
            TenantId = tenantId,
            ClaimId = claimId,
            ProcessedAt = now,
            Outcome = "Pending",
            LeaseToken = token,
        };
        try
        {
            await _col.CreateItemAsync(marker, pk, cancellationToken: ct);
            return new ClaimLease(BeginClaimOutcome.Proceed, token);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // See the Mongo implementation: terminal → duplicate; a Pending
            // marker within the lease → in progress; an expired one is taken
            // over with an ETag-conditional replace so only one retry wins.
            ItemResponse<ProcessedClaim> existing;
            try
            {
                existing = await _col.ReadItemAsync<ProcessedClaim>(id, pk, cancellationToken: ct);
            }
            catch (CosmosException nf) when (nf.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new ClaimLease(BeginClaimOutcome.InProgress, null);
            }

            if (!string.Equals(existing.Resource.Outcome, "Pending", StringComparison.Ordinal))
                return new ClaimLease(BeginClaimOutcome.AlreadyApplied, null);
            if (existing.Resource.ProcessedAt > now - _lease)
                return new ClaimLease(BeginClaimOutcome.InProgress, null);

            existing.Resource.ProcessedAt = now;
            existing.Resource.LeaseToken = token;
            try
            {
                await _col.ReplaceItemAsync(existing.Resource, id, pk,
                    new ItemRequestOptions { IfMatchEtag = existing.ETag }, ct);
                return new ClaimLease(BeginClaimOutcome.Proceed, token);
            }
            catch (CosmosException pf) when (pf.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
            {
                return new ClaimLease(BeginClaimOutcome.InProgress, null);
            }
        }
    }

    public async Task<bool> CompleteLeaseAsync(
        string tenantId, string claimId, string leaseToken, string resultingEventId, string outcome,
        string? reversalKind = null, CancellationToken ct = default)
    {
        var id = ProcessedClaim.BuildId(tenantId, claimId);
        var pk = new PartitionKey(tenantId);
        ItemResponse<ProcessedClaim> current;
        try
        {
            current = await _col.ReadItemAsync<ProcessedClaim>(id, pk, cancellationToken: ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
        var marker = current.Resource;
        if (!string.Equals(marker.Outcome, "Pending", StringComparison.Ordinal)
            || !string.Equals(marker.LeaseToken, leaseToken, StringComparison.Ordinal))
            return false;
        marker.ResultingEventId = resultingEventId;
        marker.Outcome = outcome;
        marker.ReversalKind = reversalKind;
        marker.ProcessedAt = DateTime.UtcNow;
        try
        {
            // The ETag makes the lease check and the write one atomic step.
            await _col.ReplaceItemAsync(marker, id, pk, new ItemRequestOptions { IfMatchEtag = current.ETag }, ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is System.Net.HttpStatusCode.PreconditionFailed
                                             or System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<bool> RecordLeaseTargetAsync(
        string tenantId, string claimId, string leaseToken, LeaseTarget target, CancellationToken ct = default)
    {
        var id = ProcessedClaim.BuildId(tenantId, claimId);
        var pk = new PartitionKey(tenantId);
        ItemResponse<ProcessedClaim> current;
        try
        {
            current = await _col.ReadItemAsync<ProcessedClaim>(id, pk, cancellationToken: ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
        var marker = current.Resource;
        if (!string.Equals(marker.Outcome, "Pending", StringComparison.Ordinal)
            || !string.Equals(marker.LeaseToken, leaseToken, StringComparison.Ordinal))
            return false;
        marker.TargetSnapshotId = target.SnapshotId;
        marker.TargetMemberId = target.MemberId;
        marker.TargetPlanYearStart = target.PlanYearStart;
        marker.TargetPlanYearEnd = target.PlanYearEnd;
        try
        {
            // ETag: the lease check and the write are one step (as CompleteLeaseAsync).
            await _col.ReplaceItemAsync(marker, id, pk, new ItemRequestOptions { IfMatchEtag = current.ETag }, ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is System.Net.HttpStatusCode.PreconditionFailed
                                             or System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task ReleaseAsync(string tenantId, string claimId, CancellationToken ct = default)
    {
        var id = ProcessedClaim.BuildId(tenantId, claimId);
        var pk = new PartitionKey(tenantId);
        try
        {
            var existing = await _col.ReadItemAsync<ProcessedClaim>(id, pk, cancellationToken: ct);
            if (!string.Equals(existing.Resource.Outcome, "Pending", StringComparison.Ordinal)) return;
            await _col.DeleteItemAsync<ProcessedClaim>(id, pk,
                new ItemRequestOptions { IfMatchEtag = existing.ETag }, ct);
        }
        catch (CosmosException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound
                                             or System.Net.HttpStatusCode.PreconditionFailed)
        {
            // Gone, or completed meanwhile: nothing to release.
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
