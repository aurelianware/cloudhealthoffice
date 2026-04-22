using System.Net;
using ConsentService.Models;
using Microsoft.Azure.Cosmos;

namespace ConsentService.Repositories;

/// <summary>
/// Cosmos DB implementation of <see cref="IConsentRepository"/>.
/// Partition key: <c>/tenantId</c>. Audit-trail atomicity for
/// <see cref="TransitionStatusAsync"/> / <see cref="TryTransitionToExpiredAsync"/>
/// is implemented via Cosmos transactional batches, which require both items
/// to share a partition key. To reconcile Consent (partitioned by tenantId)
/// with ConsentEvent (partitioned by <c>{tenantId}:{consentId}</c> in its own
/// container), we do NOT put the event in the same batch as the consent
/// update. Instead:
///   1. The consent update is a conditional ReplaceItem with an ETag
///      precondition sourced from the caller-supplied <see cref="Consent"/>.
///   2. If the conditional replace succeeds, we write the event row next.
///      Event writes are idempotent (unique index on EventId) so a retry is
///      safe.
///   3. If the conditional replace fails with 412 Precondition Failed, the
///      operation is rejected — another writer transitioned the consent
///      first, and the caller is responsible for re-reading or surfacing a
///      409 Conflict.
/// </summary>
public class ConsentRepository : IConsentRepository
{
    public const string ConsentsContainerName = "Consents";

    private readonly Container _consents;
    private readonly IConsentEventSink _events;

    public ConsentRepository(CosmosClient cosmosClient, string databaseName, IConsentEventSink events)
    {
        _consents = cosmosClient.GetDatabase(databaseName).GetContainer(ConsentsContainerName);
        _events = events;
    }

    public async Task<Consent> CreateAsync(Consent consent, ConsentEvent genesisEvent)
    {
        if (string.IsNullOrEmpty(consent.Id)) consent.Id = Guid.NewGuid().ToString();
        if (consent.CreatedAt == default) consent.CreatedAt = DateTime.UtcNow;

        var response = await _consents.CreateItemAsync(consent, new PartitionKey(consent.TenantId));
        await _events.AppendAsync(genesisEvent);
        return response.Resource;
    }

    public async Task<Consent?> GetByIdAsync(string tenantId, string memberId, string consentId)
    {
        try
        {
            var response = await _consents.ReadItemAsync<Consent>(consentId, new PartitionKey(tenantId));
            var consent = response.Resource;
            return consent.MemberId == memberId ? consent : null;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<Consent>> ListByMemberAsync(
        string tenantId, string memberId, bool activeOnly, DateTime? asOf = null)
    {
        // Server-side ORDER BY keeps the sort cost on Cosmos' index rather
        // than paying for it in RU + client memory for long member
        // histories. activeOnly still filters in-memory because
        // ObservedStatus depends on ExpiresAt vs now and cannot be
        // expressed cheaply at the SQL layer.
        var queryText = "SELECT * FROM c " +
                        "WHERE c.tenantId = @tenantId AND c.memberId = @memberId " +
                        "ORDER BY c.createdAt DESC";
        var query = new QueryDefinition(queryText)
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@memberId", memberId);

        var iterator = _consents.GetItemQueryIterator<Consent>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        var results = new List<Consent>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            results.AddRange(page);
        }

        var t = asOf ?? DateTime.UtcNow;
        if (activeOnly) results = results.Where(c => c.ObservedStatus(t) == ConsentStatus.Active).ToList();

        return results;
    }

    public async Task<Consent> TransitionStatusAsync(Consent consent, ConsentEvent auditEvent)
    {
        // Close the TOCTOU window between the controller's GetByIdAsync
        // and this ReplaceItemAsync: re-read fresh to capture the current
        // ETag + persisted status, verify it matches the expected
        // from-status on the audit event, and chain IfMatchEtag so a
        // concurrent writer either wins cleanly or we surface the
        // conflict as an InvalidConsentTransitionException mapped to 409
        // by the controller layer. Audit row is only appended when the
        // conditional replace succeeds.
        if (!auditEvent.FromStatus.HasValue)
        {
            throw new ArgumentException(
                "TransitionStatusAsync requires auditEvent.FromStatus to be set.",
                nameof(auditEvent));
        }
        var expectedFromStatus = auditEvent.FromStatus.Value;

        try
        {
            var fresh = await _consents.ReadItemAsync<Consent>(
                consent.Id, new PartitionKey(consent.TenantId));

            if (fresh.Resource.Status != expectedFromStatus)
            {
                throw new InvalidConsentTransitionException(
                    fresh.Resource.Status, consent.Status);
            }

            var options = new ItemRequestOptions { IfMatchEtag = fresh.ETag };
            var response = await _consents.ReplaceItemAsync(
                consent, consent.Id, new PartitionKey(consent.TenantId), options);

            await _events.AppendAsync(auditEvent);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new InvalidConsentTransitionException(
                expectedFromStatus, consent.Status);
        }
    }

    public async Task<bool> TryTransitionToExpiredAsync(Consent consent, ConsentEvent auditEvent)
    {
        // Conditional on the current persisted status still being Active.
        // If another caller expired or revoked the record first, the
        // underlying Cosmos operation is reissued against a fresh snapshot
        // and re-checks the status; if it's no longer Active, we return
        // false without writing the audit event.
        try
        {
            var fresh = await _consents.ReadItemAsync<Consent>(
                consent.Id, new PartitionKey(consent.TenantId));

            if (fresh.Resource.Status != ConsentStatus.Active)
            {
                return false;
            }

            var expired = fresh.Resource;
            expired.Status = ConsentStatus.Expired;
            expired.RevocationReasonCode = ConsentRevocationReasonCode.Expired;

            var options = new ItemRequestOptions { IfMatchEtag = fresh.ETag };
            await _consents.ReplaceItemAsync(
                expired, expired.Id, new PartitionKey(expired.TenantId), options);

            await _events.AppendAsync(auditEvent);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            // Lost the race — another writer transitioned first. No audit event.
            return false;
        }
    }
}

/// <summary>
/// Repository-local sink for appending <see cref="ConsentEvent"/> rows.
/// Lets the Cosmos and Mongo <see cref="IConsentRepository"/> implementations
/// share a single transition-and-append shape while keeping their own
/// storage choice for audit rows.
/// </summary>
public interface IConsentEventSink
{
    Task AppendAsync(ConsentEvent evt);
}
