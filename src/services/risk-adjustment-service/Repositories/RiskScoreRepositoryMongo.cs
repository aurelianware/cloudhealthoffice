using MongoDB.Bson;
using MongoDB.Driver;
using RiskAdjustmentService.Models;

namespace RiskAdjustmentService.Repositories;

public class RiskScoreRepositoryMongo : IRiskScoreRepository
{
    private readonly IMongoCollection<MemberRiskScore> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<RiskScoreRepositoryMongo> _logger;

    public RiskScoreRepositoryMongo(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor,
        ILogger<RiskScoreRepositoryMongo> logger)
    {
        _collection = database.GetCollection<MemberRiskScore>("riskScores");
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

    public async Task<MemberRiskScore?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<MemberRiskScore>.Filter.And(
            Builders<MemberRiskScore>.Filter.Eq(s => s.Id, id),
            Builders<MemberRiskScore>.Filter.Eq(s => s.TenantId, tenantId));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<MemberRiskScore?> GetByMemberAndYearAsync(string memberId, int measurementYear)
    {
        var tenantId = GetTenantId();
        var filter = Builders<MemberRiskScore>.Filter.And(
            Builders<MemberRiskScore>.Filter.Eq(s => s.TenantId, tenantId),
            Builders<MemberRiskScore>.Filter.Eq(s => s.MemberId, memberId),
            Builders<MemberRiskScore>.Filter.Eq(s => s.MeasurementYear, measurementYear));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<MemberRiskScore>> GetByMemberAsync(string memberId)
    {
        var tenantId = GetTenantId();
        var filter = Builders<MemberRiskScore>.Filter.And(
            Builders<MemberRiskScore>.Filter.Eq(s => s.TenantId, tenantId),
            Builders<MemberRiskScore>.Filter.Eq(s => s.MemberId, memberId));
        return await _collection.Find(filter)
            .SortByDescending(s => s.MeasurementYear)
            .ToListAsync();
    }

    public async Task<IEnumerable<MemberRiskScore>> SearchAsync(
        int? measurementYear,
        string? memberId,
        LineOfBusiness? lineOfBusiness,
        ScoreStatus? status,
        decimal? minScore,
        decimal? maxScore,
        int page,
        int pageSize)
    {
        var tenantId = GetTenantId();
        var builder = Builders<MemberRiskScore>.Filter;
        var filters = new List<FilterDefinition<MemberRiskScore>> { builder.Eq(s => s.TenantId, tenantId) };

        if (measurementYear.HasValue)
            filters.Add(builder.Eq(s => s.MeasurementYear, measurementYear.Value));
        if (!string.IsNullOrEmpty(memberId))
            filters.Add(builder.Eq(s => s.MemberId, memberId));
        if (lineOfBusiness.HasValue)
            filters.Add(builder.Eq(s => s.LineOfBusiness, lineOfBusiness.Value));
        if (status.HasValue)
            filters.Add(builder.Eq(s => s.Status, status.Value));
        if (minScore.HasValue)
            filters.Add(builder.Gte(s => s.RiskScore, minScore.Value));
        if (maxScore.HasValue)
            filters.Add(builder.Lte(s => s.RiskScore, maxScore.Value));

        var filter = builder.And(filters);
        return await _collection.Find(filter)
            .SortByDescending(s => s.RiskScore)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();
    }

    public async Task<IEnumerable<MemberRiskScore>> GetByMeasurementYearAsync(
        int measurementYear,
        LineOfBusiness? lineOfBusiness,
        int page,
        int pageSize)
    {
        var tenantId = GetTenantId();
        var builder = Builders<MemberRiskScore>.Filter;
        var filters = new List<FilterDefinition<MemberRiskScore>>
        {
            builder.Eq(s => s.TenantId, tenantId),
            builder.Eq(s => s.MeasurementYear, measurementYear)
        };

        if (lineOfBusiness.HasValue)
            filters.Add(builder.Eq(s => s.LineOfBusiness, lineOfBusiness.Value));

        var filter = builder.And(filters);
        return await _collection.Find(filter)
            .SortByDescending(s => s.RiskScore)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();
    }

    public async Task<MeasurementYearSummary> GetMeasurementYearSummaryAsync(
        int measurementYear,
        LineOfBusiness? lineOfBusiness)
    {
        var tenantId = GetTenantId();
        var builder = Builders<MemberRiskScore>.Filter;
        var filters = new List<FilterDefinition<MemberRiskScore>>
        {
            builder.Eq(s => s.TenantId, tenantId),
            builder.Eq(s => s.MeasurementYear, measurementYear)
        };

        if (lineOfBusiness.HasValue)
            filters.Add(builder.Eq(s => s.LineOfBusiness, lineOfBusiness.Value));

        var matchFilter = builder.And(filters);

        // Compute aggregate statistics server-side via a $group pipeline stage
        var groupStage = new BsonDocument("$group", new BsonDocument
        {
            { "_id",              BsonNull.Value },
            { "totalMembers",     new BsonDocument("$sum", 1) },
            { "scoredMembers",    new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray { new BsonDocument("$ne", new BsonArray { "$Status", (int)ScoreStatus.Rejected }), 1, 0 })) },
            { "submittedMembers", new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray { new BsonDocument("$eq", new BsonArray { "$IsSubmitted", true }), 1, 0 })) },
            { "averageRiskScore", new BsonDocument("$avg", "$RiskScore") },
            { "minRiskScore",     new BsonDocument("$min", "$RiskScore") },
            { "maxRiskScore",     new BsonDocument("$max", "$RiskScore") }
        });

        var aggResult = await _collection.Aggregate()
            .Match(matchFilter)
            .AppendStage<BsonDocument>(groupStage)
            .FirstOrDefaultAsync();

        var summary = new MeasurementYearSummary
        {
            MeasurementYear = measurementYear,
            LineOfBusiness = lineOfBusiness
        };

        if (aggResult != null)
        {
            summary.TotalMembers     = aggResult["totalMembers"].AsInt32;
            summary.ScoredMembers    = aggResult["scoredMembers"].AsInt32;
            summary.SubmittedMembers = aggResult["submittedMembers"].AsInt32;
            summary.AverageRiskScore = aggResult["averageRiskScore"].IsDouble
                ? (decimal)aggResult["averageRiskScore"].AsDouble
                : (decimal)aggResult["averageRiskScore"].AsDecimal128;
            summary.MinRiskScore = aggResult["minRiskScore"].IsDecimal128
                ? (decimal)aggResult["minRiskScore"].AsDecimal128
                : (decimal)aggResult["minRiskScore"].AsDouble;
            summary.MaxRiskScore = aggResult["maxRiskScore"].IsDecimal128
                ? (decimal)aggResult["maxRiskScore"].AsDecimal128
                : (decimal)aggResult["maxRiskScore"].AsDouble;

            // Compute median using index-based server-side queries (sort + skip + limit)
            // to avoid loading the full result set into memory.
            // For even-sized datasets the median is the average of the two middle elements.
            if (summary.TotalMembers > 0)
            {
                var midIndex = summary.TotalMembers / 2;
                var lower = await _collection.Find(matchFilter)
                    .SortBy(s => s.RiskScore)
                    .Skip(midIndex - (summary.TotalMembers % 2 == 0 ? 1 : 0))
                    .Limit(1)
                    .Project(s => s.RiskScore)
                    .FirstOrDefaultAsync();

                if (summary.TotalMembers % 2 == 0)
                {
                    var upper = await _collection.Find(matchFilter)
                        .SortBy(s => s.RiskScore)
                        .Skip(midIndex)
                        .Limit(1)
                        .Project(s => s.RiskScore)
                        .FirstOrDefaultAsync();
                    summary.MedianRiskScore = (lower + upper) / 2m;
                }
                else
                {
                    summary.MedianRiskScore = lower;
                }
            }
        }

        return summary;
    }

    public async Task<MemberRiskScore> CreateAsync(MemberRiskScore score)
    {
        score.TenantId = GetTenantId();
        await _collection.InsertOneAsync(score);
        return score;
    }

    public async Task<MemberRiskScore> UpdateAsync(MemberRiskScore score)
    {
        var tenantId = GetTenantId();
        score.TenantId = tenantId;
        var filter = Builders<MemberRiskScore>.Filter.And(
            Builders<MemberRiskScore>.Filter.Eq(s => s.Id, score.Id),
            Builders<MemberRiskScore>.Filter.Eq(s => s.TenantId, tenantId));
        await _collection.ReplaceOneAsync(filter, score);
        return score;
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<MemberRiskScore>.Filter.And(
            Builders<MemberRiskScore>.Filter.Eq(s => s.Id, id),
            Builders<MemberRiskScore>.Filter.Eq(s => s.TenantId, tenantId));
        await _collection.DeleteOneAsync(filter);
    }
}
