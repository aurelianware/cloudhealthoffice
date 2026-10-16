using MongoDB.Bson;
using MongoDB.Driver;
using PaymentService.Models;

namespace PaymentService.Repositories;

public class PaymentRunRepositoryMongo : IPaymentRunRepository
{
    private readonly IMongoCollection<PaymentRun> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<PaymentRunRepositoryMongo> _logger;

    public PaymentRunRepositoryMongo(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor,
        ILogger<PaymentRunRepositoryMongo> logger)
    {
        _collection = database.GetCollection<PaymentRun>("PaymentRuns");
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    private string GetTenantId()
    {
        var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
            throw new InvalidOperationException("TenantId not found in request context");
        return tenantId;
    }

    public async Task<PaymentRun?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<PaymentRun>.Filter.And(
            Builders<PaymentRun>.Filter.Eq(x => x.Id, id),
            Builders<PaymentRun>.Filter.Eq(x => x.TenantId, tenantId));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<PaymentRun?> GetByPaymentRunNumberAsync(string paymentRunNumber)
    {
        var tenantId = GetTenantId();
        var filter = Builders<PaymentRun>.Filter.And(
            Builders<PaymentRun>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<PaymentRun>.Filter.Eq(x => x.PaymentRunNumber, paymentRunNumber));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<PaymentRun>> SearchAsync(DateTime from, DateTime to, PaymentRunStatus? status = null)
    {
        var tenantId = GetTenantId();
        var filters = new List<FilterDefinition<PaymentRun>>
        {
            Builders<PaymentRun>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<PaymentRun>.Filter.Gte(x => x.CreatedAt, from),
            Builders<PaymentRun>.Filter.Lte(x => x.CreatedAt, to)
        };

        if (status.HasValue)
            filters.Add(Builders<PaymentRun>.Filter.Eq(x => x.Status, status.Value));

        return await _collection
            .Find(Builders<PaymentRun>.Filter.And(filters))
            .SortByDescending(x => x.CreatedAt)
            .ToListAsync();
    }

    public async Task<PaymentRun> CreateAsync(PaymentRun paymentRun)
    {
        paymentRun.TenantId = GetTenantId();
        paymentRun.CreatedAt = DateTime.UtcNow;
        paymentRun.Revision = 0;
        await _collection.InsertOneAsync(paymentRun);
        _logger.LogInformation("Created payment run {PaymentRunNumber}", paymentRun.PaymentRunNumber);
        return paymentRun;
    }

    /// <summary>Every write to a run document bumps its <see cref="PaymentRun.Revision"/>.</summary>
    private static UpdateDefinition<PaymentRun> Bumped(UpdateDefinition<PaymentRun> update) =>
        update.Inc(x => x.Revision, 1L);

    /// <summary>The document still has the revision the write was based on (0 matches documents written before the field existed).</summary>
    private static FilterDefinition<PaymentRun> RevisionIs(long expected)
    {
        var b = Builders<PaymentRun>.Filter;
        return expected == 0
            ? b.Or(b.Eq(x => x.Revision, 0L), b.Exists(x => x.Revision, false))
            : b.Eq(x => x.Revision, expected);
    }

    public async Task<bool> TryStartAsync(string id, string executedBy, DateTime startedAt)
    {
        var tenantId = GetTenantId();
        var filter = Builders<PaymentRun>.Filter.And(
            Builders<PaymentRun>.Filter.Eq(x => x.Id, id),
            Builders<PaymentRun>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<PaymentRun>.Filter.Eq(x => x.Status, PaymentRunStatus.Pending));
        var update = Builders<PaymentRun>.Update
            .Set(x => x.Status, PaymentRunStatus.Running)
            .Set(x => x.ExecutedBy, executedBy)
            .Set(x => x.ExecutionStartedAt, startedAt);
        var result = await _collection.UpdateOneAsync(filter, Bumped(update));
        return result.ModifiedCount == 1;
    }

    public async Task<bool> RecordReservationOutcomesAsync(string id, ReservationOutcomes outcomes)
    {
        var tenantId = GetTenantId();
        var filter = Builders<PaymentRun>.Filter.And(
            Builders<PaymentRun>.Filter.Eq(x => x.Id, id),
            Builders<PaymentRun>.Filter.Eq(x => x.TenantId, tenantId));

        // Two partial updates (one array cannot be pulled from and pushed to in
        // one update); neither touches status or results.
        var touched = outcomes.TouchedClaimIds;
        if (touched.Count > 0)
        {
            var pulled = await _collection.UpdateOneAsync(filter, Bumped(Builders<PaymentRun>.Update.PullFilter(
                x => x.ReservationsNeedingAttention,
                Builders<ReservationAttention>.Filter.In(a => a.ClaimId, touched))));
            if (pulled.MatchedCount == 0)
                return false;
        }

        var updates = new List<UpdateDefinition<PaymentRun>>();
        if (outcomes.Released.Count > 0)
            updates.Add(Builders<PaymentRun>.Update.AddToSetEach(x => x.ReleasedReservationClaimIds, outcomes.Released));
        if (outcomes.Attention.Count > 0)
            updates.Add(Builders<PaymentRun>.Update.PushEach(x => x.ReservationsNeedingAttention, outcomes.Attention));
        if (outcomes.Warnings.Count > 0)
            updates.Add(Builders<PaymentRun>.Update.PushEach(x => x.Warnings, outcomes.Warnings));
        if (updates.Count == 0)
            return true;

        var result = await _collection.UpdateOneAsync(filter, Bumped(Builders<PaymentRun>.Update.Combine(updates)));
        return result.MatchedCount == 1;
    }

    public async Task<bool> TrySaveEftFileAsync(string id, PaymentRunEftFile file, string? expectedSha256,
        IReadOnlyList<CheckFallbackPayment> addFallbacks, IReadOnlyList<string> addWarnings,
        IReadOnlyList<PaymentFileOutboxMessage>? addGlEvents = null)
    {
        var tenantId = GetTenantId();
        var f = Builders<PaymentRun>.Filter;
        var filter = f.And(
            f.Eq(x => x.Id, id),
            f.Eq(x => x.TenantId, tenantId),
            expectedSha256 == null
                ? f.Eq(x => x.EftFile, null) // also matches a missing field
                : f.Eq(x => x.EftFile!.Sha256, expectedSha256));
        var updates = new List<UpdateDefinition<PaymentRun>> { Builders<PaymentRun>.Update.Set(x => x.EftFile, file) };
        if (addFallbacks.Count > 0)
            updates.Add(Builders<PaymentRun>.Update.PushEach(x => x.CheckFallbacks, addFallbacks));
        if (addWarnings.Count > 0)
            updates.Add(Builders<PaymentRun>.Update.PushEach(x => x.Warnings, addWarnings));
        if (addGlEvents is { Count: > 0 })
        {
            // Never a second outbox entry with an id already there: the save then does not apply (the caller re-reads).
            filter &= f.Nin("GlOutbox.EventId", addGlEvents.Select(e => e.EventId));
            updates.Add(Builders<PaymentRun>.Update.PushEach(x => x.GlOutbox, addGlEvents));
        }
        var result = await _collection.UpdateOneAsync(filter, Bumped(Builders<PaymentRun>.Update.Combine(updates)));
        return result.MatchedCount == 1;
    }

    public async Task<bool> TryRepinEftFileAsync(string id, PaymentRunEftFile file, PaymentRunEftFile superseded, IReadOnlyList<string>? addWarnings = null,
        IReadOnlyList<PaymentFileOutboxMessage>? addGlEvents = null)
    {
        var tenantId = GetTenantId();
        var f = Builders<PaymentRun>.Filter;
        var filter = f.And(f.Eq(x => x.Id, id), f.Eq(x => x.TenantId, tenantId), f.Eq(x => x.EftFile!.Sha256, superseded.Sha256));
        var update = Bumped(Builders<PaymentRun>.Update.Set(x => x.EftFile, file).Push(x => x.EftFileHistory, superseded));
        if (addWarnings is { Count: > 0 })
            update = update.PushEach(x => x.Warnings, addWarnings);
        if (addGlEvents is { Count: > 0 })
        {
            filter &= f.Nin("GlOutbox.EventId", addGlEvents.Select(e => e.EventId));
            update = update.PushEach(x => x.GlOutbox, addGlEvents);
        }
        var result = await _collection.UpdateOneAsync(filter, update);
        return result.MatchedCount == 1;
    }

    public async Task<bool> TryRecordRemainderReissueAsync(string id, string paymentId, DateTime at, string by, string method, string reference,
        PaymentFileOutboxMessage message, string warning)
    {
        var tenantId = GetTenantId();
        var raw = _collection.Database.GetCollection<BsonDocument>(_collection.CollectionNamespace.CollectionName);
        // Matches only while that notice has no reissue and no outbox entry has the event's id:
        // of two concurrent reissues exactly one applies.
        var filter = new BsonDocument
        {
            { "_id", id },
            { "TenantId", tenantId },
            { "EftFile.VoidedPayments", new BsonDocument("$elemMatch", new BsonDocument { { "PaymentId", paymentId }, { "RemainderReissuedAt", BsonNull.Value } }) },
            { "GlOutbox.EventId", new BsonDocument("$ne", message.EventId) },
        };
        var update = new BsonDocument
        {
            { "$set", new BsonDocument
                {
                    { "EftFile.VoidedPayments.$[v].RemainderReissuedAt", at },
                    { "EftFile.VoidedPayments.$[v].RemainderReissuedBy", by },
                    { "EftFile.VoidedPayments.$[v].RemainderReissueMethod", method },
                    { "EftFile.VoidedPayments.$[v].RemainderReissueReference", reference },
                } },
            { "$push", new BsonDocument { { "GlOutbox", message.ToBsonDocument() }, { "Warnings", warning } } },
            { "$inc", new BsonDocument(nameof(PaymentRun.Revision), 1L) },
        };
        var options = new UpdateOptions
        {
            ArrayFilters = new[] { new BsonDocumentArrayFilterDefinition<BsonDocument>(new BsonDocument("v.PaymentId", paymentId)) },
        };
        var result = await raw.UpdateOneAsync(filter, update, options);
        return result.ModifiedCount == 1;
    }

    public async Task<PaymentRun> UpdateAsync(PaymentRun paymentRun)
    {
        var filter = Builders<PaymentRun>.Filter.And(
            Builders<PaymentRun>.Filter.Eq(x => x.Id, paymentRun.Id),
            Builders<PaymentRun>.Filter.Eq(x => x.TenantId, paymentRun.TenantId));

        // Conditional on the revision the caller read: a targeted write in
        // between (Pending -> Running, reservation outcomes, the EFT file pin,
        // a remainder reissue) or another save makes this one fail instead of
        // being silently undone by the whole-run replace.
        var expected = paymentRun.Revision;
        paymentRun.Revision = expected + 1;
        ReplaceOneResult result;
        try
        {
            result = await _collection.ReplaceOneAsync(filter & RevisionIs(expected), paymentRun);
        }
        catch
        {
            paymentRun.Revision = expected;
            throw;
        }

        if (result.MatchedCount == 0)
        {
            paymentRun.Revision = expected;
            if (await _collection.Find(filter).AnyAsync())
                throw new PaymentConcurrencyException("Payment run", paymentRun.Id, expected);
            // Missing: as before, nothing is written and the run is returned.
        }

        _logger.LogInformation("Updated payment run {PaymentRunNumber}", paymentRun.PaymentRunNumber);
        return paymentRun;
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<PaymentRun>.Filter.And(
            Builders<PaymentRun>.Filter.Eq(x => x.Id, id),
            Builders<PaymentRun>.Filter.Eq(x => x.TenantId, tenantId));
        await _collection.DeleteOneAsync(filter);
        _logger.LogInformation("Deleted payment run {Id}", id);
    }
}
