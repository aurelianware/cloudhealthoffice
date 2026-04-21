using MongoDB.Driver;
using ArService.Models;

namespace ArService.Repositories;

/// <summary>
/// Helper to ensure MongoDB indexes are only created once per collection per process lifetime,
/// even though repositories are registered as Scoped (per-request).
/// </summary>
internal static class IndexGuard
{
    private static readonly HashSet<string> _created = new();
    private static readonly object _lock = new();

    public static void EnsureOnce(string collectionName, Action createIndexes)
    {
        if (_created.Contains(collectionName)) return;
        lock (_lock)
        {
            if (_created.Contains(collectionName)) return;
            createIndexes();
            _created.Add(collectionName);
        }
    }
}

public class MongoGlAccountRepository : IGlAccountRepository
{
    private readonly IMongoCollection<GlAccount> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<MongoGlAccountRepository> _logger;

    public MongoGlAccountRepository(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor,
        ILogger<MongoGlAccountRepository> logger)
    {
        _collection = database.GetCollection<GlAccount>("gl_accounts");
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;

        IndexGuard.EnsureOnce("gl_accounts", CreateIndexes);
    }

    private void CreateIndexes()
    {
        var keys = Builders<GlAccount>.IndexKeys;
        var models = new List<CreateIndexModel<GlAccount>>
        {
            new CreateIndexModel<GlAccount>(keys.Ascending(x => x.TenantId).Ascending(x => x.AccountNumber)),
            new CreateIndexModel<GlAccount>(keys.Ascending(x => x.TenantId).Ascending(x => x.AccountType)),
            new CreateIndexModel<GlAccount>(keys.Ascending(x => x.TenantId).Ascending(x => x.Status))
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

    public async Task<GlAccount?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<GlAccount>.Filter.And(
            Builders<GlAccount>.Filter.Eq(x => x.Id, id),
            Builders<GlAccount>.Filter.Eq(x => x.TenantId, tenantId));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<GlAccount>> SearchAsync(
        GlAccountType? accountType = null,
        LineOfBusiness? lob = null,
        GlAccountStatus? status = null,
        int page = 1,
        int pageSize = 50)
    {
        var tenantId = GetTenantId();
        var filters = new List<FilterDefinition<GlAccount>>
        {
            Builders<GlAccount>.Filter.Eq(x => x.TenantId, tenantId)
        };

        if (accountType.HasValue)
            filters.Add(Builders<GlAccount>.Filter.Eq(x => x.AccountType, accountType.Value));
        if (lob.HasValue)
            filters.Add(Builders<GlAccount>.Filter.AnyEq(x => x.LineOfBusinessMapping, lob.Value));
        if (status.HasValue)
            filters.Add(Builders<GlAccount>.Filter.Eq(x => x.Status, status.Value));

        return await _collection
            .Find(Builders<GlAccount>.Filter.And(filters))
            .SortByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();
    }

    public async Task<GlAccount> CreateAsync(GlAccount account)
    {
        account.TenantId = GetTenantId();
        account.CreatedAt = DateTime.UtcNow;
        account.LastUpdatedAt = DateTime.UtcNow;
        await _collection.InsertOneAsync(account);
        _logger.LogInformation("Created GL account {AccountNumber}", SanitizeForLog(account.AccountNumber));
        return account;
    }

    public async Task<GlAccount> UpdateAsync(GlAccount account)
    {
        account.LastUpdatedAt = DateTime.UtcNow;
        var filter = Builders<GlAccount>.Filter.And(
            Builders<GlAccount>.Filter.Eq(x => x.Id, account.Id),
            Builders<GlAccount>.Filter.Eq(x => x.TenantId, account.TenantId));
        await _collection.ReplaceOneAsync(filter, account);
        _logger.LogInformation("Updated GL account {AccountNumber}", SanitizeForLog(account.AccountNumber));
        return account;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

public class MongoArBalanceRepository : IArBalanceRepository
{
    private readonly IMongoCollection<ArBalance> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<MongoArBalanceRepository> _logger;

    public MongoArBalanceRepository(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor,
        ILogger<MongoArBalanceRepository> logger)
    {
        _collection = database.GetCollection<ArBalance>("ar_balances");
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;

        IndexGuard.EnsureOnce("ar_balances", CreateIndexes);
    }

    private void CreateIndexes()
    {
        var keys = Builders<ArBalance>.IndexKeys;
        var models = new List<CreateIndexModel<ArBalance>>
        {
            new CreateIndexModel<ArBalance>(keys.Ascending(x => x.TenantId).Ascending(x => x.GlAccountId)),
            new CreateIndexModel<ArBalance>(keys.Ascending(x => x.TenantId).Ascending(x => x.Period)),
            new CreateIndexModel<ArBalance>(keys.Ascending(x => x.TenantId).Ascending(x => x.IsReconciled)),
            // Member-scoped lookup path — backs GetBalancesContainingMemberAsync
            // and the /members/{id}/ar-summary endpoint. The field path uses
            // the default Mongo C# driver serialization (PascalCase property
            // names) so ElemMatch queries land on this index.
            new CreateIndexModel<ArBalance>(keys.Ascending(x => x.TenantId).Ascending("PostingEntries.MemberId"))
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

    public async Task<ArBalance?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<ArBalance>.Filter.And(
            Builders<ArBalance>.Filter.Eq(x => x.Id, id),
            Builders<ArBalance>.Filter.Eq(x => x.TenantId, tenantId));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<ArBalance>> SearchAsync(
        string? accountId = null,
        DateTime? period = null,
        bool? isReconciled = null,
        int page = 1,
        int pageSize = 50)
    {
        var tenantId = GetTenantId();
        var filters = new List<FilterDefinition<ArBalance>>
        {
            Builders<ArBalance>.Filter.Eq(x => x.TenantId, tenantId)
        };

        if (!string.IsNullOrEmpty(accountId))
            filters.Add(Builders<ArBalance>.Filter.Eq(x => x.GlAccountId, accountId));
        if (period.HasValue)
            filters.Add(Builders<ArBalance>.Filter.Eq(x => x.Period, period.Value));
        if (isReconciled.HasValue)
            filters.Add(Builders<ArBalance>.Filter.Eq(x => x.IsReconciled, isReconciled.Value));

        return await _collection
            .Find(Builders<ArBalance>.Filter.And(filters))
            .SortByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();
    }

    public async Task<IEnumerable<ArBalance>> GetByAccountIdAsync(string accountId)
    {
        var tenantId = GetTenantId();
        var filter = Builders<ArBalance>.Filter.And(
            Builders<ArBalance>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<ArBalance>.Filter.Eq(x => x.GlAccountId, accountId));
        return await _collection.Find(filter).SortByDescending(x => x.Period).ToListAsync();
    }

    public async Task<ArBalance> CreateAsync(ArBalance balance)
    {
        balance.TenantId = GetTenantId();
        balance.CreatedAt = DateTime.UtcNow;
        balance.LastUpdatedAt = DateTime.UtcNow;
        await _collection.InsertOneAsync(balance);
        _logger.LogInformation("Created AR balance for account {AccountId} period {Period}",
            SanitizeForLog(balance.GlAccountId), balance.Period.ToString("yyyy-MM"));
        return balance;
    }

    public async Task<ArBalance> UpdateAsync(ArBalance balance)
    {
        balance.LastUpdatedAt = DateTime.UtcNow;
        var filter = Builders<ArBalance>.Filter.And(
            Builders<ArBalance>.Filter.Eq(x => x.Id, balance.Id),
            Builders<ArBalance>.Filter.Eq(x => x.TenantId, balance.TenantId));
        await _collection.ReplaceOneAsync(filter, balance);
        _logger.LogInformation("Updated AR balance {BalanceId}", SanitizeForLog(balance.Id));
        return balance;
    }

    public async Task<IEnumerable<ArBalance>> GetBalancesContainingMemberAsync(string memberId)
    {
        var tenantId = GetTenantId();
        var filter = Builders<ArBalance>.Filter.And(
            Builders<ArBalance>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<ArBalance>.Filter.ElemMatch(
                x => x.PostingEntries,
                e => e.MemberId == memberId));

        return await _collection.Find(filter)
            .SortByDescending(b => b.Period)
            .ToListAsync();
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

public class MongoCashPostingRepository : ICashPostingRepository
{
    private readonly IMongoCollection<CashPosting> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<MongoCashPostingRepository> _logger;

    public MongoCashPostingRepository(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor,
        ILogger<MongoCashPostingRepository> logger)
    {
        _collection = database.GetCollection<CashPosting>("cash_postings");
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;

        IndexGuard.EnsureOnce("cash_postings", CreateIndexes);
    }

    private void CreateIndexes()
    {
        var keys = Builders<CashPosting>.IndexKeys;
        var models = new List<CreateIndexModel<CashPosting>>
        {
            new CreateIndexModel<CashPosting>(keys.Ascending(x => x.TenantId).Ascending(x => x.PayerType)),
            new CreateIndexModel<CashPosting>(keys.Ascending(x => x.TenantId).Ascending(x => x.Status)),
            new CreateIndexModel<CashPosting>(keys.Ascending(x => x.TenantId).Ascending(x => x.ReceiptDate))
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

    public async Task<CashPosting?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<CashPosting>.Filter.And(
            Builders<CashPosting>.Filter.Eq(x => x.Id, id),
            Builders<CashPosting>.Filter.Eq(x => x.TenantId, tenantId));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<CashPosting>> SearchAsync(
        PayerType? payerType = null,
        CashPostingStatus? status = null,
        DateTime? dateFrom = null,
        DateTime? dateTo = null,
        int page = 1,
        int pageSize = 50)
    {
        var tenantId = GetTenantId();
        var filters = new List<FilterDefinition<CashPosting>>
        {
            Builders<CashPosting>.Filter.Eq(x => x.TenantId, tenantId)
        };

        if (payerType.HasValue)
            filters.Add(Builders<CashPosting>.Filter.Eq(x => x.PayerType, payerType.Value));
        if (status.HasValue)
            filters.Add(Builders<CashPosting>.Filter.Eq(x => x.Status, status.Value));
        if (dateFrom.HasValue)
            filters.Add(Builders<CashPosting>.Filter.Gte(x => x.ReceiptDate, dateFrom.Value));
        if (dateTo.HasValue)
            filters.Add(Builders<CashPosting>.Filter.Lte(x => x.ReceiptDate, dateTo.Value));

        return await _collection
            .Find(Builders<CashPosting>.Filter.And(filters))
            .SortByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();
    }

    public async Task<CashPosting> CreateAsync(CashPosting posting)
    {
        posting.TenantId = GetTenantId();
        posting.CreatedAt = DateTime.UtcNow;
        posting.LastUpdatedAt = DateTime.UtcNow;
        await _collection.InsertOneAsync(posting);
        _logger.LogInformation("Created cash posting {PostingNumber}", SanitizeForLog(posting.PostingNumber));
        return posting;
    }

    public async Task<CashPosting> UpdateAsync(CashPosting posting)
    {
        posting.LastUpdatedAt = DateTime.UtcNow;
        var filter = Builders<CashPosting>.Filter.And(
            Builders<CashPosting>.Filter.Eq(x => x.Id, posting.Id),
            Builders<CashPosting>.Filter.Eq(x => x.TenantId, posting.TenantId));
        await _collection.ReplaceOneAsync(filter, posting);
        _logger.LogInformation("Updated cash posting {PostingNumber}", SanitizeForLog(posting.PostingNumber));
        return posting;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

public class MongoArAdjustmentRepository : IArAdjustmentRepository
{
    private readonly IMongoCollection<ArAdjustment> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<MongoArAdjustmentRepository> _logger;

    public MongoArAdjustmentRepository(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor,
        ILogger<MongoArAdjustmentRepository> logger)
    {
        _collection = database.GetCollection<ArAdjustment>("ar_adjustments");
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;

        IndexGuard.EnsureOnce("ar_adjustments", CreateIndexes);
    }

    private void CreateIndexes()
    {
        var keys = Builders<ArAdjustment>.IndexKeys;
        var models = new List<CreateIndexModel<ArAdjustment>>
        {
            new CreateIndexModel<ArAdjustment>(keys.Ascending(x => x.TenantId).Ascending(x => x.AdjustmentType)),
            new CreateIndexModel<ArAdjustment>(keys.Ascending(x => x.TenantId).Ascending(x => x.Status)),
            new CreateIndexModel<ArAdjustment>(keys.Ascending(x => x.TenantId).Ascending(x => x.GlAccountId)),
            new CreateIndexModel<ArAdjustment>(keys.Ascending(x => x.TenantId).Ascending(x => x.Period))
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

    public async Task<ArAdjustment?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<ArAdjustment>.Filter.And(
            Builders<ArAdjustment>.Filter.Eq(x => x.Id, id),
            Builders<ArAdjustment>.Filter.Eq(x => x.TenantId, tenantId));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<ArAdjustment>> SearchAsync(
        ArAdjustmentType? type = null,
        ArAdjustmentStatus? status = null,
        DateTime? period = null,
        string? glAccountId = null,
        int page = 1,
        int pageSize = 50)
    {
        var tenantId = GetTenantId();
        var filters = new List<FilterDefinition<ArAdjustment>>
        {
            Builders<ArAdjustment>.Filter.Eq(x => x.TenantId, tenantId)
        };

        if (type.HasValue)
            filters.Add(Builders<ArAdjustment>.Filter.Eq(x => x.AdjustmentType, type.Value));
        if (status.HasValue)
            filters.Add(Builders<ArAdjustment>.Filter.Eq(x => x.Status, status.Value));
        if (period.HasValue)
            filters.Add(Builders<ArAdjustment>.Filter.Eq(x => x.Period, period.Value));
        if (!string.IsNullOrEmpty(glAccountId))
            filters.Add(Builders<ArAdjustment>.Filter.Eq(x => x.GlAccountId, glAccountId));

        return await _collection
            .Find(Builders<ArAdjustment>.Filter.And(filters))
            .SortByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();
    }

    public async Task<ArAdjustment> CreateAsync(ArAdjustment adjustment)
    {
        adjustment.TenantId = GetTenantId();
        adjustment.CreatedAt = DateTime.UtcNow;
        adjustment.LastUpdatedAt = DateTime.UtcNow;
        await _collection.InsertOneAsync(adjustment);
        _logger.LogInformation("Created AR adjustment {AdjustmentNumber}", SanitizeForLog(adjustment.AdjustmentNumber));
        return adjustment;
    }

    public async Task<ArAdjustment> UpdateAsync(ArAdjustment adjustment)
    {
        adjustment.LastUpdatedAt = DateTime.UtcNow;
        var filter = Builders<ArAdjustment>.Filter.And(
            Builders<ArAdjustment>.Filter.Eq(x => x.Id, adjustment.Id),
            Builders<ArAdjustment>.Filter.Eq(x => x.TenantId, adjustment.TenantId));
        await _collection.ReplaceOneAsync(filter, adjustment);
        _logger.LogInformation("Updated AR adjustment {AdjustmentNumber}", SanitizeForLog(adjustment.AdjustmentNumber));
        return adjustment;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

public class MongoArBatchRuleRepository : IArBatchRuleRepository
{
    private readonly IMongoCollection<ArBatchRule> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<MongoArBatchRuleRepository> _logger;

    public MongoArBatchRuleRepository(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor,
        ILogger<MongoArBatchRuleRepository> logger)
    {
        _collection = database.GetCollection<ArBatchRule>("ar_batch_rules");
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;

        IndexGuard.EnsureOnce("ar_batch_rules", CreateIndexes);
    }

    private void CreateIndexes()
    {
        var keys = Builders<ArBatchRule>.IndexKeys;
        var models = new List<CreateIndexModel<ArBatchRule>>
        {
            new CreateIndexModel<ArBatchRule>(keys.Ascending(x => x.TenantId).Ascending(x => x.Trigger)),
            new CreateIndexModel<ArBatchRule>(keys.Ascending(x => x.TenantId).Ascending(x => x.Status)),
            new CreateIndexModel<ArBatchRule>(keys.Ascending(x => x.TenantId).Ascending(x => x.ExecutionOrder))
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

    public async Task<ArBatchRule?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<ArBatchRule>.Filter.And(
            Builders<ArBatchRule>.Filter.Eq(x => x.Id, id),
            Builders<ArBatchRule>.Filter.Eq(x => x.TenantId, tenantId));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<ArBatchRule>> SearchAsync(
        BatchRuleTrigger? trigger = null,
        BatchRuleStatus? status = null,
        int page = 1,
        int pageSize = 50)
    {
        var tenantId = GetTenantId();
        var filters = new List<FilterDefinition<ArBatchRule>>
        {
            Builders<ArBatchRule>.Filter.Eq(x => x.TenantId, tenantId)
        };

        if (trigger.HasValue)
            filters.Add(Builders<ArBatchRule>.Filter.Eq(x => x.Trigger, trigger.Value));
        if (status.HasValue)
            filters.Add(Builders<ArBatchRule>.Filter.Eq(x => x.Status, status.Value));

        return await _collection
            .Find(Builders<ArBatchRule>.Filter.And(filters))
            .SortByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();
    }

    public async Task<ArBatchRule> CreateAsync(ArBatchRule rule)
    {
        rule.TenantId = GetTenantId();
        rule.CreatedAt = DateTime.UtcNow;
        rule.LastUpdatedAt = DateTime.UtcNow;
        await _collection.InsertOneAsync(rule);
        _logger.LogInformation("Created batch rule {RuleCode}", SanitizeForLog(rule.RuleCode));
        return rule;
    }

    public async Task<ArBatchRule> UpdateAsync(ArBatchRule rule)
    {
        rule.LastUpdatedAt = DateTime.UtcNow;
        var filter = Builders<ArBatchRule>.Filter.And(
            Builders<ArBatchRule>.Filter.Eq(x => x.Id, rule.Id),
            Builders<ArBatchRule>.Filter.Eq(x => x.TenantId, rule.TenantId));
        await _collection.ReplaceOneAsync(filter, rule);
        _logger.LogInformation("Updated batch rule {RuleCode}", SanitizeForLog(rule.RuleCode));
        return rule;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
