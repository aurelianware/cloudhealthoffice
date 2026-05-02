using MongoDB.Driver;
using PaymentService.Models;

namespace PaymentService.Repositories;

public interface IEraEnvelopeRepository
{
    Task<EraEnvelopeRecord?> GetByIdAsync(string id);
    Task<IEnumerable<EraEnvelopeRecord>> GetByPaymentRunIdAsync(string paymentRunId);
    /// <summary>
    /// 5.12b — fetch envelopes produced by a given <c>ReversalRun</c>.
    /// Mirror of <see cref="GetByPaymentRunIdAsync"/> for the reversal
    /// path; returns the empty sequence for a run id that has not yet
    /// (or did not) emit any envelopes.
    /// </summary>
    Task<IEnumerable<EraEnvelopeRecord>> GetByReversalRunIdAsync(string reversalRunId);
    Task<IEnumerable<EraEnvelopeRecord>> SearchAsync(string? paymentRunId, string? tradingPartnerId, string? reversalRunId = null);
    Task<EraEnvelopeRecord> CreateAsync(EraEnvelopeRecord record);
}

public class EraEnvelopeRepositoryMongo : IEraEnvelopeRepository
{
    private readonly IMongoCollection<EraEnvelopeRecord> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<EraEnvelopeRepositoryMongo> _logger;

    public EraEnvelopeRepositoryMongo(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor,
        ILogger<EraEnvelopeRepositoryMongo> logger)
    {
        _collection = database.GetCollection<EraEnvelopeRecord>("EraEnvelopes");
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

    public async Task<EraEnvelopeRecord?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<EraEnvelopeRecord>.Filter.And(
            Builders<EraEnvelopeRecord>.Filter.Eq(x => x.Id, id),
            Builders<EraEnvelopeRecord>.Filter.Eq(x => x.TenantId, tenantId));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<EraEnvelopeRecord>> GetByPaymentRunIdAsync(string paymentRunId)
    {
        var tenantId = GetTenantId();
        var filter = Builders<EraEnvelopeRecord>.Filter.And(
            Builders<EraEnvelopeRecord>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<EraEnvelopeRecord>.Filter.Eq(x => x.PaymentRunId, paymentRunId));
        return await _collection.Find(filter).SortBy(x => x.TradingPartnerId).ToListAsync();
    }

    public async Task<IEnumerable<EraEnvelopeRecord>> GetByReversalRunIdAsync(string reversalRunId)
    {
        var tenantId = GetTenantId();
        var filter = Builders<EraEnvelopeRecord>.Filter.And(
            Builders<EraEnvelopeRecord>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<EraEnvelopeRecord>.Filter.Eq(x => x.ReversalRunId, reversalRunId));
        return await _collection.Find(filter).SortBy(x => x.TradingPartnerId).ToListAsync();
    }

    public async Task<IEnumerable<EraEnvelopeRecord>> SearchAsync(string? paymentRunId, string? tradingPartnerId, string? reversalRunId = null)
    {
        var tenantId = GetTenantId();
        var filters = new List<FilterDefinition<EraEnvelopeRecord>>
        {
            Builders<EraEnvelopeRecord>.Filter.Eq(x => x.TenantId, tenantId)
        };

        if (!string.IsNullOrEmpty(paymentRunId))
            filters.Add(Builders<EraEnvelopeRecord>.Filter.Eq(x => x.PaymentRunId, paymentRunId));
        if (!string.IsNullOrEmpty(tradingPartnerId))
            filters.Add(Builders<EraEnvelopeRecord>.Filter.Eq(x => x.TradingPartnerId, tradingPartnerId));
        if (!string.IsNullOrEmpty(reversalRunId))
            filters.Add(Builders<EraEnvelopeRecord>.Filter.Eq(x => x.ReversalRunId, reversalRunId));

        return await _collection
            .Find(Builders<EraEnvelopeRecord>.Filter.And(filters))
            .SortByDescending(x => x.GeneratedAt)
            .ToListAsync();
    }

    public async Task<EraEnvelopeRecord> CreateAsync(EraEnvelopeRecord record)
    {
        record.TenantId = GetTenantId();
        record.GeneratedAt = record.GeneratedAt == default ? DateTime.UtcNow : record.GeneratedAt;
        await _collection.InsertOneAsync(record);
        _logger.LogInformation(
            "Created EraEnvelope {Id} for PaymentRun {PaymentRunId} ({ClaimCount} claims, ${Amount:F2})",
            SanitizeForLog(record.Id), SanitizeForLog(record.PaymentRunId),
            record.ClaimCount, record.TotalPaymentAmount);
        return record;
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}

/// <summary>
/// In-memory fallback used when no MongoDB instance is available
/// (Cosmos-only deployments). EraEnvelope records are not retained
/// across restarts on this path; mirrors the Cosmos-only deployment
/// posture for payment-service in 5.10 (Mongo is the canonical
/// payment-service backing store).
/// </summary>
public class InMemoryEraEnvelopeRepository : IEraEnvelopeRepository
{
    private readonly List<EraEnvelopeRecord> _records = new();
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly object _lock = new();

    public InMemoryEraEnvelopeRepository(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private string GetTenantId()
    {
        var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
            throw new InvalidOperationException("TenantId not found in request context");
        return tenantId;
    }

    public Task<EraEnvelopeRecord?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        lock (_lock)
        {
            var match = _records.FirstOrDefault(r => r.Id == id && r.TenantId == tenantId);
            return Task.FromResult<EraEnvelopeRecord?>(match);
        }
    }

    public Task<IEnumerable<EraEnvelopeRecord>> GetByPaymentRunIdAsync(string paymentRunId)
    {
        var tenantId = GetTenantId();
        lock (_lock)
        {
            var matches = _records
                .Where(r => r.TenantId == tenantId && r.PaymentRunId == paymentRunId)
                .OrderBy(r => r.TradingPartnerId)
                .ToList();
            return Task.FromResult<IEnumerable<EraEnvelopeRecord>>(matches);
        }
    }

    public Task<IEnumerable<EraEnvelopeRecord>> GetByReversalRunIdAsync(string reversalRunId)
    {
        var tenantId = GetTenantId();
        lock (_lock)
        {
            var matches = _records
                .Where(r => r.TenantId == tenantId && r.ReversalRunId == reversalRunId)
                .OrderBy(r => r.TradingPartnerId)
                .ToList();
            return Task.FromResult<IEnumerable<EraEnvelopeRecord>>(matches);
        }
    }

    public Task<IEnumerable<EraEnvelopeRecord>> SearchAsync(string? paymentRunId, string? tradingPartnerId, string? reversalRunId = null)
    {
        var tenantId = GetTenantId();
        lock (_lock)
        {
            var matches = _records
                .Where(r => r.TenantId == tenantId)
                .Where(r => string.IsNullOrEmpty(paymentRunId) || r.PaymentRunId == paymentRunId)
                .Where(r => string.IsNullOrEmpty(tradingPartnerId) || r.TradingPartnerId == tradingPartnerId)
                .Where(r => string.IsNullOrEmpty(reversalRunId) || r.ReversalRunId == reversalRunId)
                .OrderByDescending(r => r.GeneratedAt)
                .ToList();
            return Task.FromResult<IEnumerable<EraEnvelopeRecord>>(matches);
        }
    }

    public Task<EraEnvelopeRecord> CreateAsync(EraEnvelopeRecord record)
    {
        record.TenantId = GetTenantId();
        record.GeneratedAt = record.GeneratedAt == default ? DateTime.UtcNow : record.GeneratedAt;
        lock (_lock)
        {
            _records.Add(record);
        }
        return Task.FromResult(record);
    }
}
