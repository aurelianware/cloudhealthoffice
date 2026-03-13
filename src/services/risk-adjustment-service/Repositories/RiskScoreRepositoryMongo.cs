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

        var filter = builder.And(filters);
        var scores = await _collection.Find(filter).ToListAsync();

        var summary = new MeasurementYearSummary
        {
            MeasurementYear = measurementYear,
            LineOfBusiness = lineOfBusiness,
            TotalMembers = scores.Count,
            ScoredMembers = scores.Count(s => s.Status != ScoreStatus.Rejected),
            SubmittedMembers = scores.Count(s => s.IsSubmitted),
            AverageRiskScore = scores.Count > 0 ? scores.Average(s => s.RiskScore) : 0,
            MinRiskScore = scores.Count > 0 ? scores.Min(s => s.RiskScore) : 0,
            MaxRiskScore = scores.Count > 0 ? scores.Max(s => s.RiskScore) : 0
        };

        if (scores.Count > 0)
        {
            var sorted = scores.OrderBy(s => s.RiskScore).ToList();
            summary.MedianRiskScore = sorted[sorted.Count / 2].RiskScore;
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
        score.TenantId = GetTenantId();
        var filter = Builders<MemberRiskScore>.Filter.Eq(s => s.Id, score.Id);
        await _collection.ReplaceOneAsync(filter, score);
        return score;
    }

    public async Task DeleteAsync(string id)
    {
        var filter = Builders<MemberRiskScore>.Filter.Eq(s => s.Id, id);
        await _collection.DeleteOneAsync(filter);
    }
}
