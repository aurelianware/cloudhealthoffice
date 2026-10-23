using System.Collections.Concurrent;
using ContactService.Models;
using MongoDB.Driver;

namespace ContactService.Repositories;

/// <summary>
/// MongoDB store for contacts. The tenant comes from the request context and
/// is part of every filter; a whole-document replace is fenced by the
/// revision the caller read.
/// </summary>
public sealed class ContactRepositoryMongo : IContactRepository
{
    private readonly IMongoCollection<Contact> _collection;
    private readonly ContactIndexes _indexes;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ContactRepositoryMongo(
        IMongoDatabase database,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ContactIndexes? indexes = null)
    {
        var collectionName = configuration["MongoDb:ContactsCollection"] ?? "Contacts";
        _collection = database.GetCollection<Contact>(collectionName);
        _httpContextAccessor = httpContextAccessor;
        _indexes = indexes ?? new ContactIndexes();
    }

    public Task EnsureIndexesAsync(CancellationToken ct = default) => _indexes.EnsureAsync(_collection, ct);

    private string GetTenantId() =>
        _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString() is { Length: > 0 } tenantId
            ? tenantId
            : throw new InvalidOperationException("TenantId not found in request context");

    private static FilterDefinitionBuilder<Contact> Filter => Builders<Contact>.Filter;

    private static int Clamp(int limit) => Math.Clamp(limit, 1, 1000);

    public async Task<Contact?> GetByIdAsync(string id, CancellationToken ct = default) =>
        await _collection
            .Find(Filter.Eq(c => c.Id, id) & Filter.Eq(c => c.TenantId, GetTenantId()))
            .FirstOrDefaultAsync(ct);

    public async Task<Contact?> GetByContactNumberAsync(string contactNumber, CancellationToken ct = default) =>
        await _collection
            .Find(Filter.Eq(c => c.ContactNumber, contactNumber) & Filter.Eq(c => c.TenantId, GetTenantId()))
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<Contact>> ListByMemberAsync(
        string memberId, int limit = 100, CancellationToken ct = default) =>
        await _collection
            .Find(Filter.Eq(c => c.TenantId, GetTenantId()) & Filter.Eq(c => c.MemberId, memberId))
            .SortByDescending(c => c.StartedAt)
            .Limit(Clamp(limit))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Contact>> ListByProviderAsync(
        string npi, int limit = 100, CancellationToken ct = default) =>
        await _collection
            .Find(Filter.Eq(c => c.TenantId, GetTenantId()) & Filter.Eq(c => c.ProviderNpi, npi))
            .SortByDescending(c => c.StartedAt)
            .Limit(Clamp(limit))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Contact>> ListBySubjectAsync(
        ContactSubjectType type, string subjectId, int limit = 100, CancellationToken ct = default) =>
        await _collection
            .Find(Filter.Eq(c => c.TenantId, GetTenantId())
                & Filter.ElemMatch(c => c.Subjects,
                    Builders<ContactSubject>.Filter.Eq(s => s.Type, type)
                    & Builders<ContactSubject>.Filter.Eq(s => s.SubjectId, subjectId)))
            .SortByDescending(c => c.StartedAt)
            .Limit(Clamp(limit))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Contact>> ListByAgentAsync(
        string agentId, bool openOnly = false, int limit = 100, CancellationToken ct = default)
    {
        var filter = Filter.Eq(c => c.TenantId, GetTenantId()) & Filter.Eq(c => c.AgentId, agentId);
        if (openOnly) filter &= Filter.Eq(c => c.Status, ContactStatus.Open);

        return await _collection.Find(filter)
            .SortByDescending(c => c.StartedAt)
            .Limit(Clamp(limit))
            .ToListAsync(ct);
    }

    public async Task<Contact> CreateAsync(Contact contact, CancellationToken ct = default)
    {
        await EnsureIndexesAsync(ct);

        contact.TenantId = GetTenantId();
        contact.Revision = 1;
        contact.CreatedAt = DateTime.UtcNow;
        contact.UpdatedAt = contact.CreatedAt;

        try
        {
            await _collection.InsertOneAsync(contact, cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new DuplicateContactNumberException(contact.ContactNumber);
        }

        return contact;
    }

    public async Task<Contact> UpdateAsync(Contact contact, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var expected = contact.Revision;

        contact.TenantId = tenantId;
        contact.Revision = expected + 1;
        contact.UpdatedAt = DateTime.UtcNow;

        var result = await _collection.ReplaceOneAsync(
            Filter.Eq(c => c.Id, contact.Id)
                & Filter.Eq(c => c.TenantId, tenantId)
                & Filter.Eq(c => c.Revision, expected),
            contact,
            cancellationToken: ct);

        if (result.MatchedCount == 0)
        {
            // Put the revision back so a caller that retries from the same
            // object does not race forward past what it actually read.
            contact.Revision = expected;
            throw new ContactConcurrencyException(contact.Id, expected);
        }

        return contact;
    }
}

/// <summary>
/// Creates the contact indexes once per collection, whichever writer gets
/// there first.
/// </summary>
public sealed class ContactIndexes
{
    private readonly ConcurrentDictionary<(IMongoClient Client, string Collection), Lazy<Task>> _ready = new();

    public async Task EnsureAsync(IMongoCollection<Contact> collection, CancellationToken ct = default)
    {
        var key = (collection.Database.Client, collection.CollectionNamespace.FullName);
        var work = _ready.GetOrAdd(key, _ => new Lazy<Task>(() => CreateAsync(collection),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            // A request cancelling must not cancel the initialization shared by other writers.
            await work.Value.WaitAsync(ct);
        }
        catch when (work.IsValueCreated && work.Value.IsFaulted)
        {
            ((ICollection<KeyValuePair<(IMongoClient, string), Lazy<Task>>>)_ready)
                .Remove(new KeyValuePair<(IMongoClient, string), Lazy<Task>>(key, work));
            throw;
        }
    }

    private static async Task CreateAsync(IMongoCollection<Contact> collection)
    {
        var keys = Builders<Contact>.IndexKeys;
        await collection.Indexes.CreateManyAsync([
            new CreateIndexModel<Contact>(keys.Ascending(c => c.TenantId).Ascending(c => c.ContactNumber),
                new CreateIndexOptions { Name = "contact_tenant_number_unique", Unique = true }),
            // The member's contact history, and the provider's.
            new CreateIndexModel<Contact>(
                keys.Ascending(c => c.TenantId).Ascending(c => c.MemberId).Descending(c => c.StartedAt),
                new CreateIndexOptions { Name = "contact_tenant_member_started" }),
            new CreateIndexModel<Contact>(
                keys.Ascending(c => c.TenantId).Ascending(c => c.ProviderNpi).Descending(c => c.StartedAt),
                new CreateIndexOptions { Name = "contact_tenant_provider_started" }),
            // "Who called about this claim": the multikey path into Subjects.
            new CreateIndexModel<Contact>(
                keys.Ascending(c => c.TenantId)
                    .Ascending("Subjects.SubjectId")
                    .Ascending("Subjects.Type"),
                new CreateIndexOptions { Name = "contact_tenant_subject" }),
            // The rep's own recent work, and what they left open.
            new CreateIndexModel<Contact>(
                keys.Ascending(c => c.TenantId).Ascending(c => c.AgentId)
                    .Ascending(c => c.Status).Descending(c => c.StartedAt),
                new CreateIndexOptions { Name = "contact_tenant_agent_status_started" }),
            // Reporting reads by period: volume, handle time, reason trends.
            new CreateIndexModel<Contact>(
                keys.Ascending(c => c.TenantId).Descending(c => c.StartedAt),
                new CreateIndexOptions { Name = "contact_tenant_started" }),
        ]);
    }
}
