using MongoDB.Driver;
using CapitationService.Models;

namespace CapitationService.Repositories;

public class CapitationStatementRepositoryMongo : ICapitationStatementRepository
{
    private readonly IMongoCollection<CapitationStatement> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<CapitationStatementRepositoryMongo> _logger;

    public CapitationStatementRepositoryMongo(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor,
        ILogger<CapitationStatementRepositoryMongo> logger)
    {
        _collection = database.GetCollection<CapitationStatement>("capitation-statements");
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;

        CreateIndexes();
    }

    private void CreateIndexes()
    {
        var keys = Builders<CapitationStatement>.IndexKeys;
        var models = new List<CreateIndexModel<CapitationStatement>>
        {
            new CreateIndexModel<CapitationStatement>(keys.Ascending(s => s.TenantId).Ascending(s => s.ProviderNPI)),
            new CreateIndexModel<CapitationStatement>(keys.Ascending(s => s.TenantId).Ascending(s => s.CapitationRunId)),
            new CreateIndexModel<CapitationStatement>(keys.Ascending(s => s.TenantId).Ascending(s => s.Status)),
            new CreateIndexModel<CapitationStatement>(keys.Ascending(s => s.TenantId).Ascending(s => s.CapitationPeriodStart))
        };
        _collection.Indexes.CreateMany(models);
    }

    private string GetTenantId()
    {
        var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
            throw new InvalidOperationException("TenantId not found in request context");
        return tenantId;
    }

    public async Task<CapitationStatement?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<CapitationStatement>.Filter.And(
            Builders<CapitationStatement>.Filter.Eq(x => x.Id, id),
            Builders<CapitationStatement>.Filter.Eq(x => x.TenantId, tenantId));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<CapitationStatement>> GetByRunIdAsync(string runId)
    {
        var tenantId = GetTenantId();
        var filter = Builders<CapitationStatement>.Filter.And(
            Builders<CapitationStatement>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<CapitationStatement>.Filter.Eq(x => x.CapitationRunId, runId));
        return await _collection.Find(filter).SortBy(x => x.ProviderName).ToListAsync();
    }

    public async Task<IEnumerable<CapitationStatement>> GetByProviderNpiAsync(string npi, DateTime? periodFrom = null, DateTime? periodTo = null)
    {
        var tenantId = GetTenantId();
        var filters = new List<FilterDefinition<CapitationStatement>>
        {
            Builders<CapitationStatement>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<CapitationStatement>.Filter.Eq(x => x.ProviderNPI, npi)
        };

        if (periodFrom.HasValue)
            filters.Add(Builders<CapitationStatement>.Filter.Gte(x => x.CapitationPeriodStart, periodFrom.Value));
        if (periodTo.HasValue)
            filters.Add(Builders<CapitationStatement>.Filter.Lte(x => x.CapitationPeriodStart, periodTo.Value));

        return await _collection
            .Find(Builders<CapitationStatement>.Filter.And(filters))
            .SortByDescending(x => x.CapitationPeriodStart)
            .ToListAsync();
    }

    public async Task<IEnumerable<CapitationStatement>> GetByStatusAsync(CapitationStatementStatus status)
    {
        var tenantId = GetTenantId();
        var filter = Builders<CapitationStatement>.Filter.And(
            Builders<CapitationStatement>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<CapitationStatement>.Filter.Eq(x => x.Status, status));
        return await _collection.Find(filter).SortByDescending(x => x.CapitationPeriodStart).ToListAsync();
    }

    public async Task<IEnumerable<CapitationStatement>> GetUnpaidStatementsAsync()
    {
        var tenantId = GetTenantId();
        var filter = Builders<CapitationStatement>.Filter.And(
            Builders<CapitationStatement>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<CapitationStatement>.Filter.Ne(x => x.Status, CapitationStatementStatus.Paid),
            Builders<CapitationStatement>.Filter.Ne(x => x.Status, CapitationStatementStatus.Voided),
            Builders<CapitationStatement>.Filter.Gt(x => x.NetPayable, 0m));
        return await _collection.Find(filter).SortBy(x => x.CapitationPeriodStart).ToListAsync();
    }

    public async Task<CapitationStatement> CreateAsync(CapitationStatement statement)
    {
        statement.TenantId = GetTenantId();
        statement.CreatedAt = DateTime.UtcNow;
        statement.LastUpdatedAt = DateTime.UtcNow;
        statement.Revision = 0;
        await _collection.InsertOneAsync(statement);
        _logger.LogInformation("Created capitation statement {StatementNumber} for provider {NPI}",
            statement.StatementNumber, statement.ProviderNPI);
        return statement;
    }

    public async Task<bool> TryStartPaymentAsync(string statementId, string disbursementId)
    {
        var tenantId = GetTenantId();
        var f = Builders<CapitationStatement>.Filter;
        var filter = f.And(
            f.Eq(x => x.Id, statementId),
            f.Eq(x => x.TenantId, tenantId),
            f.Eq(x => x.Status, CapitationStatementStatus.Approved));
        var result = await _collection.UpdateOneAsync(filter, Bumped(Builders<CapitationStatement>.Update
            .Set(x => x.Status, CapitationStatementStatus.PaymentInitiated)
            .Set(x => x.EftDisbursementId, disbursementId)
            .Set(x => x.LastUpdatedAt, DateTime.UtcNow)));
        return result.ModifiedCount == 1;
    }

    public async Task UndoStartPaymentAsync(string statementId, string disbursementId)
    {
        var tenantId = GetTenantId();
        var f = Builders<CapitationStatement>.Filter;
        var filter = f.And(
            f.Eq(x => x.Id, statementId),
            f.Eq(x => x.TenantId, tenantId),
            f.Eq(x => x.Status, CapitationStatementStatus.PaymentInitiated),
            f.Eq(x => x.EftDisbursementId, disbursementId));
        await _collection.UpdateOneAsync(filter, Bumped(Builders<CapitationStatement>.Update
            .Set(x => x.Status, CapitationStatementStatus.Approved)
            .Set(x => x.EftDisbursementId, null)
            .Set(x => x.LastUpdatedAt, DateTime.UtcNow)));
    }

    public async Task<bool> MarkPaymentUnknownAsync(string statementId, string disbursementId)
    {
        var tenantId = GetTenantId();
        var f = Builders<CapitationStatement>.Filter;
        var filter = f.And(
            f.Eq(x => x.Id, statementId),
            f.Eq(x => x.TenantId, tenantId),
            f.Eq(x => x.Status, CapitationStatementStatus.PaymentInitiated),
            f.Eq(x => x.EftDisbursementId, disbursementId));
        var result = await _collection.UpdateOneAsync(filter, Bumped(Builders<CapitationStatement>.Update
            .Set(x => x.Status, CapitationStatementStatus.PaymentUnknown)
            .Set(x => x.LastUpdatedAt, DateTime.UtcNow)));
        return result.ModifiedCount == 1;
    }

    public async Task<bool> ResolvePaymentUnknownAsync(string statementId, string disbursementId, bool paid)
    {
        var tenantId = GetTenantId();
        var f = Builders<CapitationStatement>.Filter;
        var filter = f.And(
            f.Eq(x => x.Id, statementId),
            f.Eq(x => x.TenantId, tenantId),
            f.Eq(x => x.Status, CapitationStatementStatus.PaymentUnknown),
            f.Eq(x => x.EftDisbursementId, disbursementId));
        var update = Builders<CapitationStatement>.Update
            .Set(x => x.Status, paid ? CapitationStatementStatus.PaymentInitiated : CapitationStatementStatus.Approved)
            .Set(x => x.LastUpdatedAt, DateTime.UtcNow);
        if (!paid) update = update.Set(x => x.EftDisbursementId, null);
        var result = await _collection.UpdateOneAsync(filter, Bumped(update));
        return result.ModifiedCount == 1;
    }

    /// <summary>Every targeted write to a statement bumps its <see cref="CapitationStatement.Revision"/>.</summary>
    private static UpdateDefinition<CapitationStatement> Bumped(UpdateDefinition<CapitationStatement> update) =>
        update.Inc(x => x.Revision, 1L);

    /// <summary>The document still has the revision the write was based on (0 matches documents written before the field existed).</summary>
    private static FilterDefinition<CapitationStatement> RevisionIs(long expected)
    {
        var b = Builders<CapitationStatement>.Filter;
        return expected == 0
            ? b.Or(b.Eq(x => x.Revision, 0L), b.Exists(x => x.Revision, false))
            : b.Eq(x => x.Revision, expected);
    }

    public async Task<CapitationStatement> UpdateAsync(CapitationStatement statement)
    {
        statement.LastUpdatedAt = DateTime.UtcNow;
        var filter = Builders<CapitationStatement>.Filter.And(
            Builders<CapitationStatement>.Filter.Eq(x => x.Id, statement.Id),
            Builders<CapitationStatement>.Filter.Eq(x => x.TenantId, statement.TenantId));
        // Conditional on the revision the caller read: a replace from a stale
        // read can never undo a payment transition made since (TryStartPayment,
        // MarkPaymentUnknown, ResolvePaymentUnknown, UndoStartPayment).
        var expected = statement.Revision;
        statement.Revision = expected + 1;
        ReplaceOneResult result;
        try
        {
            result = await _collection.ReplaceOneAsync(filter & RevisionIs(expected), statement);
        }
        catch
        {
            statement.Revision = expected;
            throw;
        }

        if (result.MatchedCount == 0)
        {
            statement.Revision = expected;
            if (await _collection.Find(filter).AnyAsync())
                throw new CapitationConcurrencyException("Capitation statement", statement.Id, expected);
            // Missing: as before, nothing written and no error.
        }
        _logger.LogInformation("Updated capitation statement {StatementNumber}", statement.StatementNumber);
        return statement;
    }
}
