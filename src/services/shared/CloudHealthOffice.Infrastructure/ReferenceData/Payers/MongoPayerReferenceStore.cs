using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace CloudHealthOffice.Infrastructure.ReferenceData.Payers;

/// <summary>
/// MongoDB persistence for canonical payer reference data. Global payer
/// documents are not tenant-partitioned; tenant overlays are filtered by
/// tenant id on every read.
/// </summary>
internal sealed class MongoPayerReferenceStore : IPayerReferenceStore
{
    private readonly IMongoCollection<PayerReferenceDocument> _payers;
    private readonly IMongoCollection<PayerTenantOverrideDocument> _overrides;
    private readonly IMongoCollection<PayerDirectorySyncStatus> _sync;

    public MongoPayerReferenceStore(IMongoDatabase database, PayerReferenceOptions options)
    {
        _payers = database.GetCollection<PayerReferenceDocument>(options.MongoCollectionName);
        _overrides = database.GetCollection<PayerTenantOverrideDocument>(options.MongoOverrideCollectionName);
        _sync = database.GetCollection<PayerDirectorySyncStatus>(options.MongoSyncStatusCollectionName);
    }

    public async Task EnsureIndexesAsync(CancellationToken ct)
    {
        await _payers.Indexes.CreateManyAsync(new[]
        {
            new CreateIndexModel<PayerReferenceDocument>(
                Builders<PayerReferenceDocument>.IndexKeys.Ascending(d => d.Id),
                new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<PayerReferenceDocument>(
                Builders<PayerReferenceDocument>.IndexKeys.Ascending(d => d.SearchTokens))
        }, ct).ConfigureAwait(false);

        await _overrides.Indexes.CreateOneAsync(
            new CreateIndexModel<PayerTenantOverrideDocument>(
                Builders<PayerTenantOverrideDocument>.IndexKeys
                    .Ascending(d => d.TenantId)
                    .Ascending(d => d.PayerId),
                new CreateIndexOptions { Unique = true }),
            cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<PayerReference?> GetByIdAsync(string id, CancellationToken ct)
    {
        var doc = await _payers.Find(d => d.Id == id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return doc?.ToModel();
    }

    public async Task<IReadOnlyList<PayerReference>> GetByIdsAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var idList = ids.ToList();
        if (idList.Count == 0)
        {
            return Array.Empty<PayerReference>();
        }

        var docs = await _payers.Find(d => idList.Contains(d.Id)).ToListAsync(ct).ConfigureAwait(false);
        return docs.Select(d => d.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<PayerReference>> FindExactAsync(string normalizedToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(normalizedToken))
        {
            return Array.Empty<PayerReference>();
        }

        // Mongo filters are case-insensitive via the in-memory token match after
        // a broad fetch of plausible candidates (id / alias / identifier value).
        var docs = await _payers
            .Find(d => d.SearchTokens.Contains(normalizedToken))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return docs.Select(d => d.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<PayerReference>> SearchAsync(PayerSearchQuery query, CancellationToken ct)
    {
        var filters = new List<FilterDefinition<PayerReferenceDocument>>();
        var b = Builders<PayerReferenceDocument>.Filter;

        if (query.Active is { } active)
        {
            filters.Add(b.Eq(d => d.Active, active));
        }

        if (!string.IsNullOrWhiteSpace(query.Id))
        {
            filters.Add(b.Regex(d => d.Id, new MongoDB.Bson.BsonRegularExpression($"^{Escape(query.Id.Trim())}$", "i")));
        }

        if (!string.IsNullOrWhiteSpace(query.ExternalValue))
        {
            filters.Add(b.ElemMatch(d => d.ExternalIdentifiers, i => i.Value == query.ExternalValue.Trim()));
        }

        var filter = filters.Count == 0 ? b.Empty : b.And(filters);
        var take = query.MaxResults <= 0 ? 50 : Math.Min(query.MaxResults, 500);
        var docs = await _payers.Find(filter).Limit(take * 4).ToListAsync(ct).ConfigureAwait(false);
        var models = docs.Select(d => d.ToModel()).AsEnumerable();

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var token = PayerLookup.Normalize(query.Text);
            models = models.Where(p =>
                PayerLookup.Tokens(p).Contains(token) ||
                PayerLookup.Normalize(p.Name).Contains(token, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(query.ExternalSystem) || !string.IsNullOrWhiteSpace(query.ExternalType))
        {
            models = models.Where(p => p.ExternalIdentifiers.Any(id =>
                (string.IsNullOrWhiteSpace(query.ExternalSystem) ||
                 PayerLookup.EqualsNormalized(id.System, query.ExternalSystem)) &&
                (string.IsNullOrWhiteSpace(query.ExternalType) ||
                 PayerLookup.EqualsNormalized(id.Type, query.ExternalType)) &&
                (string.IsNullOrWhiteSpace(query.ExternalValue) ||
                 PayerLookup.EqualsNormalized(id.Value, query.ExternalValue))));
        }

        return models.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Take(take).ToList();
    }

    public Task UpsertAsync(PayerReference payer, CancellationToken ct) =>
        _payers.ReplaceOneAsync(
            d => d.Id == payer.Id,
            PayerReferenceDocument.FromModel(payer),
            new ReplaceOptions { IsUpsert = true },
            ct);

    public async Task UpsertManyAsync(IReadOnlyList<PayerReference> payers, CancellationToken ct)
    {
        if (payers.Count == 0)
        {
            return;
        }

        var models = payers.Select(p =>
            new ReplaceOneModel<PayerReferenceDocument>(
                Builders<PayerReferenceDocument>.Filter.Eq(d => d.Id, p.Id),
                PayerReferenceDocument.FromModel(p))
            {
                IsUpsert = true
            });
        await _payers.BulkWriteAsync(models, cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<int> DisableMissingFromSourceAsync(
        string source, IReadOnlyCollection<string> presentIds, DateTimeOffset at, CancellationToken ct)
    {
        var filter = Builders<PayerReferenceDocument>.Filter.And(
            Builders<PayerReferenceDocument>.Filter.Eq(d => d.Source, source),
            Builders<PayerReferenceDocument>.Filter.Eq(d => d.Active, true),
            Builders<PayerReferenceDocument>.Filter.Nin(d => d.Id, presentIds));

        var update = Builders<PayerReferenceDocument>.Update
            .Set(d => d.Active, false)
            .Set(d => d.LastSyncedAt, at);

        var result = await _payers.UpdateManyAsync(filter, update, cancellationToken: ct).ConfigureAwait(false);
        return (int)result.ModifiedCount;
    }

    public async Task<PayerTenantOverride?> GetTenantOverrideAsync(
        string tenantId, string payerId, CancellationToken ct)
    {
        var doc = await _overrides
            .Find(d => d.TenantId == tenantId && d.PayerId == payerId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return doc?.ToModel();
    }

    public Task UpsertTenantOverrideAsync(PayerTenantOverride overlay, CancellationToken ct) =>
        _overrides.ReplaceOneAsync(
            d => d.TenantId == overlay.TenantId && d.PayerId == overlay.PayerId,
            PayerTenantOverrideDocument.FromModel(overlay),
            new ReplaceOptions { IsUpsert = true },
            ct);

    public async Task<IReadOnlyList<PayerTenantOverride>> ListTenantOverridesAsync(
        string tenantId, CancellationToken ct)
    {
        var docs = await _overrides.Find(d => d.TenantId == tenantId).ToListAsync(ct).ConfigureAwait(false);
        return docs.Select(d => d.ToModel()).ToList();
    }

    public async Task<PayerDirectorySyncStatus?> GetSyncStatusAsync(string source, CancellationToken ct) =>
        await _sync.Find(s => s.Source == source).FirstOrDefaultAsync(ct).ConfigureAwait(false);

    public Task SaveSyncStatusAsync(PayerDirectorySyncStatus status, CancellationToken ct) =>
        _sync.ReplaceOneAsync(
            s => s.Source == status.Source,
            status,
            new ReplaceOptions { IsUpsert = true },
            ct);

    public async Task<int> CountAsync(CancellationToken ct) =>
        (int)await _payers.CountDocumentsAsync(FilterDefinition<PayerReferenceDocument>.Empty, cancellationToken: ct)
            .ConfigureAwait(false);

    private static string Escape(string value) =>
        System.Text.RegularExpressions.Regex.Escape(value);
}

internal sealed class PayerReferenceDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public List<string> Aliases { get; set; } = new();

    public List<PayerExternalIdentifier> ExternalIdentifiers { get; set; } = new();

    public List<PayerTransactionCapability> SupportedTransactions { get; set; } = new();

    public List<PayerEnrollmentRequirement> EnrollmentRequirements { get; set; } = new();

    public bool Active { get; set; } = true;

    public string Source { get; set; } = string.Empty;

    public DateTimeOffset? SourceUpdatedAt { get; set; }

    public DateTimeOffset LastSyncedAt { get; set; }

    public Dictionary<string, string> Metadata { get; set; } = new();

    public List<string> SearchTokens { get; set; } = new();

    public static PayerReferenceDocument FromModel(PayerReference p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Aliases = p.Aliases.ToList(),
        ExternalIdentifiers = p.ExternalIdentifiers.ToList(),
        SupportedTransactions = p.SupportedTransactions.ToList(),
        EnrollmentRequirements = p.EnrollmentRequirements.ToList(),
        Active = p.Active,
        Source = p.Provenance.Source,
        SourceUpdatedAt = p.Provenance.SourceUpdatedAt,
        LastSyncedAt = p.Provenance.LastSyncedAt,
        Metadata = new Dictionary<string, string>(p.Metadata),
        SearchTokens = PayerLookup.Tokens(p).Distinct(StringComparer.Ordinal).ToList()
    };

    public PayerReference ToModel() => new()
    {
        Id = Id,
        Name = Name,
        Aliases = Aliases,
        ExternalIdentifiers = ExternalIdentifiers,
        SupportedTransactions = SupportedTransactions,
        EnrollmentRequirements = EnrollmentRequirements,
        Active = Active,
        Provenance = new PayerReferenceProvenance
        {
            Source = Source,
            SourceUpdatedAt = SourceUpdatedAt,
            LastSyncedAt = LastSyncedAt
        },
        Metadata = Metadata
    };
}

internal sealed class PayerTenantOverrideDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    public string PayerId { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public string? PreferredAlias { get; set; }

    public List<PayerExternalIdentifier> ExternalIdentifiers { get; set; } = new();

    public List<Gateways.HealthcareTransactionType> EnrolledTransactions { get; set; } = new();

    public static PayerTenantOverrideDocument FromModel(PayerTenantOverride o) => new()
    {
        Id = $"{o.TenantId}|{o.PayerId}",
        TenantId = o.TenantId,
        PayerId = o.PayerId,
        Enabled = o.Enabled,
        PreferredAlias = o.PreferredAlias,
        ExternalIdentifiers = o.ExternalIdentifiers.ToList(),
        EnrolledTransactions = o.EnrolledTransactions.ToList()
    };

    public PayerTenantOverride ToModel() => new()
    {
        TenantId = TenantId,
        PayerId = PayerId,
        Enabled = Enabled,
        PreferredAlias = PreferredAlias,
        ExternalIdentifiers = ExternalIdentifiers,
        EnrolledTransactions = EnrolledTransactions
    };
}
