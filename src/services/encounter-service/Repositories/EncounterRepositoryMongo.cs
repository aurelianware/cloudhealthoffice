using MongoDB.Driver;
using EncounterService.Models;

namespace EncounterService.Repositories;

public class EncounterRepositoryMongo : IEncounterRepository
{
    private readonly IMongoCollection<Encounter> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<EncounterRepositoryMongo> _logger;

    public EncounterRepositoryMongo(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor,
        ILogger<EncounterRepositoryMongo> logger)
    {
        _collection = database.GetCollection<Encounter>("encounters");
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

    public async Task<Encounter?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<Encounter>.Filter.And(
            Builders<Encounter>.Filter.Eq(e => e.Id, id),
            Builders<Encounter>.Filter.Eq(e => e.TenantId, tenantId));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<Encounter?> GetByControlNumberAsync(string controlNumber)
    {
        var tenantId = GetTenantId();
        var filter = Builders<Encounter>.Filter.And(
            Builders<Encounter>.Filter.Eq(e => e.EncounterControlNumber, controlNumber),
            Builders<Encounter>.Filter.Eq(e => e.TenantId, tenantId));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<Encounter>> SearchAsync(
        string? memberId,
        string? payerId,
        string? batchId,
        DateTime? serviceDateFrom,
        DateTime? serviceDateTo,
        EncounterStatus? status,
        SubmissionType? submissionType,
        LineOfBusiness? lineOfBusiness,
        int page,
        int pageSize)
    {
        var tenantId = GetTenantId();
        var builder = Builders<Encounter>.Filter;
        var filters = new List<FilterDefinition<Encounter>> { builder.Eq(e => e.TenantId, tenantId) };

        if (!string.IsNullOrEmpty(memberId))
            filters.Add(builder.Eq(e => e.MemberId, memberId));
        if (!string.IsNullOrEmpty(payerId))
            filters.Add(builder.Eq(e => e.PayerId, payerId));
        if (!string.IsNullOrEmpty(batchId))
            filters.Add(builder.Eq(e => e.BatchId, batchId));
        if (serviceDateFrom.HasValue)
            filters.Add(builder.Gte(e => e.ServiceDateFrom, serviceDateFrom.Value));
        if (serviceDateTo.HasValue)
            filters.Add(builder.Lte(e => e.ServiceDateTo, serviceDateTo.Value));
        if (status.HasValue)
            filters.Add(builder.Eq(e => e.Status, status.Value));
        if (submissionType.HasValue)
            filters.Add(builder.Eq(e => e.SubmissionType, submissionType.Value));
        if (lineOfBusiness.HasValue)
            filters.Add(builder.Eq(e => e.LineOfBusiness, lineOfBusiness.Value));

        var filter = builder.And(filters);
        return await _collection.Find(filter)
            .SortByDescending(e => e.CreatedDate)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();
    }

    public async Task<IEnumerable<Encounter>> GetPendingByPayerAsync(
        string payerId,
        LineOfBusiness? lineOfBusiness,
        EncounterType? encounterType,
        int maxCount)
    {
        var tenantId = GetTenantId();
        var builder = Builders<Encounter>.Filter;
        var filters = new List<FilterDefinition<Encounter>>
        {
            builder.Eq(e => e.TenantId, tenantId),
            builder.Eq(e => e.PayerId, payerId),
            builder.Eq(e => e.Status, EncounterStatus.Pending)
        };

        if (lineOfBusiness.HasValue)
            filters.Add(builder.Eq(e => e.LineOfBusiness, lineOfBusiness.Value));
        if (encounterType.HasValue)
            filters.Add(builder.Eq(e => e.EncounterType, encounterType.Value));

        var filter = builder.And(filters);
        return await _collection.Find(filter)
            .SortBy(e => e.CreatedDate)
            .Limit(maxCount)
            .ToListAsync();
    }

    public async Task<EncounterSummary> GetSummaryAsync(DateTime from, DateTime to, string? payerId)
    {
        var tenantId = GetTenantId();
        var builder = Builders<Encounter>.Filter;
        var filters = new List<FilterDefinition<Encounter>>
        {
            builder.Eq(e => e.TenantId, tenantId),
            builder.Gte(e => e.CreatedDate, from),
            builder.Lte(e => e.CreatedDate, to)
        };

        if (!string.IsNullOrEmpty(payerId))
            filters.Add(builder.Eq(e => e.PayerId, payerId));

        var filter = builder.And(filters);
        var encounters = await _collection.Find(filter).ToListAsync();

        var summary = new EncounterSummary
        {
            TotalEncounters = encounters.Count,
            PendingEncounters = encounters.Count(e => e.Status == EncounterStatus.Pending),
            QueuedEncounters = encounters.Count(e => e.Status == EncounterStatus.Queued),
            SubmittedEncounters = encounters.Count(e => e.Status == EncounterStatus.Submitted),
            AcceptedEncounters = encounters.Count(e => e.Status == EncounterStatus.Accepted),
            RejectedEncounters = encounters.Count(e => e.Status == EncounterStatus.Rejected),
            CorrectionEncounters = encounters.Count(e => e.SubmissionType == SubmissionType.Correction),
            TotalChargeAmount = encounters.Sum(e => e.TotalChargeAmount)
        };

        if (summary.TotalEncounters > 0)
            summary.AcceptanceRate = (decimal)summary.AcceptedEncounters / summary.TotalEncounters * 100;

        return summary;
    }

    public async Task<Encounter> CreateAsync(Encounter encounter)
    {
        encounter.TenantId = GetTenantId();
        await _collection.InsertOneAsync(encounter);
        return encounter;
    }

    public async Task<Encounter> UpdateAsync(Encounter encounter)
    {
        encounter.TenantId = GetTenantId();
        var filter = Builders<Encounter>.Filter.Eq(e => e.Id, encounter.Id);
        await _collection.ReplaceOneAsync(filter, encounter);
        return encounter;
    }

    public async Task DeleteAsync(string id)
    {
        var filter = Builders<Encounter>.Filter.Eq(e => e.Id, id);
        await _collection.DeleteOneAsync(filter);
    }
}
