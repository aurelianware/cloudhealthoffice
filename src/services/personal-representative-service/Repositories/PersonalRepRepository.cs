using System.Net;
using PersonalRepresentativeService.Models;
using PersonalRepresentativeService.Services;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace PersonalRepresentativeService.Repositories;

/// <summary>
/// Cosmos DB implementation of <see cref="IPersonalRepRepository"/>.
/// Partition key on both containers is <c>/tenantId</c>.
///
/// Audit-trail atomicity for <see cref="TransitionStatusAsync"/> and
/// <see cref="TryTransitionToInactiveAsync"/> is implemented via conditional
/// ReplaceItem with an ETag precondition sourced from a fresh read. Audit
/// rows are appended only when the conditional replace succeeds.
///
/// Association pair writes use <c>TransactionalBatch</c> — both rows of a
/// pair share the tenantId partition key, which is a precondition for
/// batch atomicity. The audit event append happens AFTER a successful
/// batch commit (it lives in a different Cosmos container and therefore
/// cannot be part of the same batch). The four failure modes are
/// documented on <see cref="IPersonalRepRepository.AddAssociationPairAsync"/>
/// and covered by <c>PersonalRepAssociationPairAtomicityTests</c>.
/// </summary>
public class PersonalRepRepository : IPersonalRepRepository
{
    public const string PersonalRepsContainerName = "PersonalRepresentatives";
    public const string PersonalRepAssociationsContainerName = "PersonalRepAssociations";

    private readonly Container _reps;
    private readonly Container _associations;
    private readonly IPersonalRepEventSink _events;
    private readonly ILogger<PersonalRepRepository> _logger;

    public PersonalRepRepository(
        CosmosClient cosmosClient,
        string databaseName,
        IPersonalRepEventSink events,
        ILogger<PersonalRepRepository> logger)
    {
        var database = cosmosClient.GetDatabase(databaseName);
        _reps = database.GetContainer(PersonalRepsContainerName);
        _associations = database.GetContainer(PersonalRepAssociationsContainerName);
        _events = events;
        _logger = logger;
    }

    public async Task<PersonalRepresentative> CreateAsync(
        PersonalRepresentative rep, PersonalRepEvent genesisEvent, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(rep.Id)) rep.Id = Guid.NewGuid().ToString();
        if (rep.CreatedAt == default) rep.CreatedAt = DateTime.UtcNow;

        var response = await _reps.CreateItemAsync(rep, new PartitionKey(rep.TenantId), cancellationToken: ct);
        await _events.AppendAsync(genesisEvent);
        return response.Resource;
    }

    public async Task<PersonalRepresentative?> GetByIdAsync(
        string tenantId, string repId, CancellationToken ct = default)
    {
        try
        {
            var response = await _reps.ReadItemAsync<PersonalRepresentative>(
                repId, new PartitionKey(tenantId), cancellationToken: ct);
            var rep = response.Resource;
            return rep.IsDeleted ? null : rep;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<PersonalRepresentative>> GetByIdsAsync(
        string tenantId, IReadOnlyList<string> repIds, CancellationToken ct = default)
    {
        if (repIds.Count == 0) return Array.Empty<PersonalRepresentative>();

        var deduped = repIds.Distinct().ToList();
        // Build an IN(...) query — safe because values are Guids passed by
        // the service itself, not raw user input.
        var paramList = string.Join(",", deduped.Select((_, i) => $"@id{i}"));
        var qd = new QueryDefinition(
            $"SELECT * FROM c WHERE c.tenantId = @tenantId " +
            $"AND c.id IN ({paramList}) " +
            "AND (NOT IS_DEFINED(c.deletedAt) OR c.deletedAt = null)")
            .WithParameter("@tenantId", tenantId);
        for (var i = 0; i < deduped.Count; i++)
            qd = qd.WithParameter($"@id{i}", deduped[i]);

        var iterator = _reps.GetItemQueryIterator<PersonalRepresentative>(
            qd,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        var results = new List<PersonalRepresentative>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page.Where(r => !r.IsDeleted));
        }
        return results;
    }

    public async Task<IReadOnlyList<PersonalRepresentative>> ListByTenantAsync(
        string tenantId, bool activeOnly = false, DateTime? asOf = null, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId " +
            "AND (NOT IS_DEFINED(c.deletedAt) OR c.deletedAt = null) " +
            "ORDER BY c.createdAt DESC")
            .WithParameter("@tenantId", tenantId);

        var iterator = _reps.GetItemQueryIterator<PersonalRepresentative>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        var results = new List<PersonalRepresentative>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page);
        }

        var t = asOf ?? DateTime.UtcNow;
        if (activeOnly)
            results = results.Where(r => r.ObservedStatus(t) == PersonalRepStatus.Active).ToList();

        return results;
    }

    public async Task<PersonalRepresentative> TransitionStatusAsync(
        PersonalRepresentative rep, PersonalRepEvent auditEvent, CancellationToken ct = default)
    {
        if (!auditEvent.FromStatus.HasValue)
        {
            throw new ArgumentException(
                "TransitionStatusAsync requires auditEvent.FromStatus to be set.",
                nameof(auditEvent));
        }
        var expectedFromStatus = auditEvent.FromStatus.Value;

        try
        {
            var fresh = await _reps.ReadItemAsync<PersonalRepresentative>(
                rep.Id, new PartitionKey(rep.TenantId), cancellationToken: ct);

            if (fresh.Resource.Status != expectedFromStatus)
            {
                throw new InvalidPersonalRepTransitionException(
                    fresh.Resource.Status, rep.Status);
            }

            var options = new ItemRequestOptions { IfMatchEtag = fresh.ETag };
            var response = await _reps.ReplaceItemAsync(
                rep, rep.Id, new PartitionKey(rep.TenantId), options, cancellationToken: ct);

            await _events.AppendAsync(auditEvent);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new InvalidPersonalRepTransitionException(
                expectedFromStatus, rep.Status);
        }
    }

    public async Task<PersonalRepresentative?> TryTransitionToInactiveAsync(
        PersonalRepresentative rep, PersonalRepEvent auditEvent)
    {
        try
        {
            var fresh = await _reps.ReadItemAsync<PersonalRepresentative>(
                rep.Id, new PartitionKey(rep.TenantId));

            if (fresh.Resource.Status != PersonalRepStatus.Active)
            {
                return null;
            }

            var inactivated = fresh.Resource;
            inactivated.Status = PersonalRepStatus.Inactive;
            inactivated.InactivationReasonCode = PersonalRepInactivationReasonCode.Expired;
            inactivated.InactivatedAt = DateTime.UtcNow;

            var options = new ItemRequestOptions { IfMatchEtag = fresh.ETag };
            await _reps.ReplaceItemAsync(
                inactivated, inactivated.Id, new PartitionKey(inactivated.TenantId), options);

            await _events.AppendAsync(auditEvent);
            return inactivated;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return null;
        }
    }

    public async Task AddAssociationPairAsync(
        PersonalRepAssociation forward,
        PersonalRepAssociation inverse,
        PersonalRepEvent auditEvent,
        CancellationToken ct = default)
    {
        EnsureSameTenant(forward, inverse);

        // Cosmos TransactionalBatch requires both items to share the
        // partition key (tenantId). Both pair rows always do — EnsureSameTenant
        // above is the structural guard.
        var batch = _associations.CreateTransactionalBatch(new PartitionKey(forward.TenantId))
            .CreateItem(forward)
            .CreateItem(inverse);

        using var response = await batch.ExecuteAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new CosmosException(
                $"PersonalRepAssociation pair create failed (pairId={forward.PairId}): {response.ErrorMessage}",
                response.StatusCode, 0, response.ActivityId, response.RequestCharge);
        }

        // Post-commit audit append. If this throws, the pair IS persisted
        // but the audit row is missing — a compliance-visible gap. Log at
        // Error so operations can reconcile, then rethrow so the caller
        // sees a 500.
        try
        {
            await _events.AppendAsync(auditEvent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Personal Rep association pair committed but audit append failed " +
                "(tenantId={TenantId}, pairId={PairId}, eventId={EventId}, correlationId={CorrelationId}). " +
                "Pair persisted; audit row missing — manual reconciliation required.",
                LogSanitizer.SafeForLog(forward.TenantId),
                LogSanitizer.SafeForLog(forward.PairId),
                LogSanitizer.SafeForLog(auditEvent.EventId),
                LogSanitizer.SafeForLog(auditEvent.CorrelationId));
            throw;
        }
    }

    public async Task RemoveAssociationPairAsync(
        string tenantId,
        string pairId,
        string removedBy,
        PersonalRepEvent auditEvent,
        CancellationToken ct = default)
    {
        // Read both rows first so we can soft-delete both in one batch.
        var rows = await QueryPairAsync(tenantId, pairId, ct);
        if (rows.Count == 0) return;

        var now = DateTime.UtcNow;
        var batch = _associations.CreateTransactionalBatch(new PartitionKey(tenantId));
        foreach (var row in rows)
        {
            row.EffectiveTo ??= now;
            row.UpdatedAt = now;
            row.UpdatedBy = removedBy;
            batch = batch.ReplaceItem(row.Id, row);
        }

        using var response = await batch.ExecuteAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new CosmosException(
                $"PersonalRepAssociation pair remove failed (pairId={pairId}): {response.ErrorMessage}",
                response.StatusCode, 0, response.ActivityId, response.RequestCharge);
        }

        try
        {
            await _events.AppendAsync(auditEvent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Personal Rep association pair removed but audit append failed " +
                "(tenantId={TenantId}, pairId={PairId}, eventId={EventId}, correlationId={CorrelationId}). " +
                "Pair soft-deleted; audit row missing — manual reconciliation required.",
                LogSanitizer.SafeForLog(tenantId),
                LogSanitizer.SafeForLog(pairId),
                LogSanitizer.SafeForLog(auditEvent.EventId),
                LogSanitizer.SafeForLog(auditEvent.CorrelationId));
            throw;
        }
    }

    public async Task<IReadOnlyList<PersonalRepAssociation>> ListAssociationsForMemberAsync(
        string tenantId, string memberId, bool activeOnly = false, DateTime? asOf = null,
        CancellationToken ct = default)
    {
        var query = new QueryDefinition(@"
                SELECT * FROM c
                WHERE c.tenantId = @t
                  AND c.memberId = @m
                  AND c.direction = @d
                  AND (NOT IS_DEFINED(c.deletedAt) OR c.deletedAt = null)")
            .WithParameter("@t", tenantId)
            .WithParameter("@m", memberId)
            .WithParameter("@d", (int)AssociationDirection.MemberToRep);

        var results = await QueryAssociationsAsync(query, tenantId, ct);

        var t = asOf ?? DateTime.UtcNow;
        if (activeOnly)
            results = results.Where(a =>
                a.EffectiveFrom <= t && (a.EffectiveTo == null || a.EffectiveTo > t)).ToList();

        return results;
    }

    public async Task<IReadOnlyList<PersonalRepAssociation>> ListAssociationsForRepAsync(
        string tenantId, string repId, bool activeOnly = false, DateTime? asOf = null,
        CancellationToken ct = default)
    {
        var query = new QueryDefinition(@"
                SELECT * FROM c
                WHERE c.tenantId = @t
                  AND c.repId = @r
                  AND c.direction = @d
                  AND (NOT IS_DEFINED(c.deletedAt) OR c.deletedAt = null)")
            .WithParameter("@t", tenantId)
            .WithParameter("@r", repId)
            .WithParameter("@d", (int)AssociationDirection.RepToMember);

        var results = await QueryAssociationsAsync(query, tenantId, ct);

        var t = asOf ?? DateTime.UtcNow;
        if (activeOnly)
            results = results.Where(a =>
                a.EffectiveFrom <= t && (a.EffectiveTo == null || a.EffectiveTo > t)).ToList();

        return results;
    }

    public async Task<PersonalRepAssociation?> FindActiveAssociationAsync(
        string tenantId, string repId, string memberId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var query = new QueryDefinition(@"
                SELECT TOP 1 * FROM c
                WHERE c.tenantId = @t
                  AND c.repId = @r
                  AND c.memberId = @m
                  AND c.direction = @d
                  AND (NOT IS_DEFINED(c.deletedAt) OR c.deletedAt = null)
                  AND (NOT IS_DEFINED(c.effectiveTo) OR c.effectiveTo = null OR c.effectiveTo > @now)")
            .WithParameter("@t", tenantId)
            .WithParameter("@r", repId)
            .WithParameter("@m", memberId)
            .WithParameter("@d", (int)AssociationDirection.RepToMember)
            .WithParameter("@now", now);

        var results = await QueryAssociationsAsync(query, tenantId, ct);
        return results.FirstOrDefault();
    }

    private async Task<List<PersonalRepAssociation>> QueryPairAsync(
        string tenantId, string pairId, CancellationToken ct)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @t AND c.pairId = @p")
            .WithParameter("@t", tenantId)
            .WithParameter("@p", pairId);
        return await QueryAssociationsAsync(query, tenantId, ct);
    }

    private async Task<List<PersonalRepAssociation>> QueryAssociationsAsync(
        QueryDefinition query, string tenantId, CancellationToken ct)
    {
        var iterator = _associations.GetItemQueryIterator<PersonalRepAssociation>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        var results = new List<PersonalRepAssociation>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page);
        }
        return results;
    }

    private static void EnsureSameTenant(PersonalRepAssociation a, PersonalRepAssociation b)
    {
        if (!string.Equals(a.TenantId, b.TenantId, StringComparison.Ordinal))
        {
            // Cross-tenant representative portability is intentionally NOT
            // supported (out-of-scope item 3). Both pair rows must share
            // the same Cosmos partition key so TransactionalBatch atomicity
            // holds.
            throw new InvalidOperationException(
                "PersonalRepAssociation pair rows must share the same TenantId. " +
                "Cross-tenant representatives are not supported.");
        }
    }
}
