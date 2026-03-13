using Microsoft.Azure.Cosmos;
using RiskAdjustmentService.Models;

namespace RiskAdjustmentService.Repositories;

public interface IRiskScoreRepository
{
    Task<MemberRiskScore?> GetByIdAsync(string id);
    Task<MemberRiskScore?> GetByMemberAndYearAsync(string memberId, int measurementYear);
    Task<IEnumerable<MemberRiskScore>> GetByMemberAsync(string memberId);
    Task<IEnumerable<MemberRiskScore>> SearchAsync(
        int? measurementYear,
        string? memberId,
        LineOfBusiness? lineOfBusiness,
        ScoreStatus? status,
        decimal? minScore,
        decimal? maxScore,
        int page,
        int pageSize);
    Task<IEnumerable<MemberRiskScore>> GetByMeasurementYearAsync(
        int measurementYear,
        LineOfBusiness? lineOfBusiness,
        int page,
        int pageSize);
    Task<MeasurementYearSummary> GetMeasurementYearSummaryAsync(
        int measurementYear,
        LineOfBusiness? lineOfBusiness);
    Task<MemberRiskScore> CreateAsync(MemberRiskScore score);
    Task<MemberRiskScore> UpdateAsync(MemberRiskScore score);
    Task DeleteAsync(string id);
}

public class RiskScoreRepository : IRiskScoreRepository
{
    private readonly Container _container;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<RiskScoreRepository> _logger;

    public RiskScoreRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<RiskScoreRepository> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "RiskAdjustmentDB";
        var containerName = configuration["CosmosDb:ContainerName"] ?? "RiskScores";

        _container = cosmosClient.GetContainer(databaseName, containerName);
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
        try
        {
            var response = await _container.ReadItemAsync<MemberRiskScore>(id, new PartitionKey(id));
            if (response.Resource.TenantId != tenantId)
                return null;
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<MemberRiskScore?> GetByMemberAndYearAsync(string memberId, int measurementYear)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.memberId = @memberId AND c.measurementYear = @year")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@memberId", memberId)
            .WithParameter("@year", measurementYear);

        var iterator = _container.GetItemQueryIterator<MemberRiskScore>(query);
        var results = new List<MemberRiskScore>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }
        return results.FirstOrDefault();
    }

    public async Task<IEnumerable<MemberRiskScore>> GetByMemberAsync(string memberId)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.memberId = @memberId ORDER BY c.measurementYear DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@memberId", memberId);

        var iterator = _container.GetItemQueryIterator<MemberRiskScore>(query);
        var results = new List<MemberRiskScore>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }
        return results;
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
        var conditions = new List<string> { "c.tenantId = @tenantId" };
        var parameters = new Dictionary<string, object> { { "@tenantId", tenantId } };

        if (measurementYear.HasValue)
        {
            conditions.Add("c.measurementYear = @measurementYear");
            parameters["@measurementYear"] = measurementYear.Value;
        }
        if (!string.IsNullOrEmpty(memberId))
        {
            conditions.Add("c.memberId = @memberId");
            parameters["@memberId"] = memberId;
        }
        if (lineOfBusiness.HasValue)
        {
            conditions.Add("c.lineOfBusiness = @lineOfBusiness");
            parameters["@lineOfBusiness"] = lineOfBusiness.Value.ToString();
        }
        if (status.HasValue)
        {
            conditions.Add("c.status = @status");
            parameters["@status"] = status.Value.ToString();
        }
        if (minScore.HasValue)
        {
            conditions.Add("c.riskScore >= @minScore");
            parameters["@minScore"] = minScore.Value;
        }
        if (maxScore.HasValue)
        {
            conditions.Add("c.riskScore <= @maxScore");
            parameters["@maxScore"] = maxScore.Value;
        }

        var queryText = $@"
            SELECT * FROM c
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY c.riskScore DESC
            OFFSET {(page - 1) * pageSize} LIMIT {pageSize}";

        var queryDef = new QueryDefinition(queryText);
        foreach (var (key, value) in parameters)
            queryDef.WithParameter(key, value);

        var iterator = _container.GetItemQueryIterator<MemberRiskScore>(queryDef);
        var results = new List<MemberRiskScore>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }
        return results;
    }

    public async Task<IEnumerable<MemberRiskScore>> GetByMeasurementYearAsync(
        int measurementYear,
        LineOfBusiness? lineOfBusiness,
        int page,
        int pageSize)
    {
        var tenantId = GetTenantId();
        var lobCondition = lineOfBusiness.HasValue
            ? "AND c.lineOfBusiness = @lineOfBusiness"
            : "";

        var queryText = $@"
            SELECT * FROM c
            WHERE c.tenantId = @tenantId
            AND c.measurementYear = @measurementYear
            {lobCondition}
            ORDER BY c.riskScore DESC
            OFFSET {(page - 1) * pageSize} LIMIT {pageSize}";

        var queryDef = new QueryDefinition(queryText)
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@measurementYear", measurementYear);

        if (lineOfBusiness.HasValue)
            queryDef.WithParameter("@lineOfBusiness", lineOfBusiness.Value.ToString());

        var iterator = _container.GetItemQueryIterator<MemberRiskScore>(queryDef);
        var results = new List<MemberRiskScore>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }
        return results;
    }

    public async Task<MeasurementYearSummary> GetMeasurementYearSummaryAsync(
        int measurementYear,
        LineOfBusiness? lineOfBusiness)
    {
        var tenantId = GetTenantId();
        var lobCondition = lineOfBusiness.HasValue
            ? "AND c.lineOfBusiness = @lineOfBusiness"
            : "";

        var queryText = $@"
            SELECT
                COUNT(1) as TotalMembers,
                SUM(CASE WHEN c.status != 'Rejected' THEN 1 ELSE 0 END) as ScoredMembers,
                SUM(CASE WHEN c.isSubmitted = true THEN 1 ELSE 0 END) as SubmittedMembers,
                AVG(c.riskScore) as AverageRiskScore,
                MIN(c.riskScore) as MinRiskScore,
                MAX(c.riskScore) as MaxRiskScore
            FROM c
            WHERE c.tenantId = @tenantId
            AND c.measurementYear = @measurementYear
            {lobCondition}";

        var queryDef = new QueryDefinition(queryText)
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@measurementYear", measurementYear);

        if (lineOfBusiness.HasValue)
            queryDef.WithParameter("@lineOfBusiness", lineOfBusiness.Value.ToString());

        var iterator = _container.GetItemQueryIterator<MeasurementYearProjection>(queryDef);
        var summary = new MeasurementYearSummary
        {
            MeasurementYear = measurementYear,
            LineOfBusiness = lineOfBusiness
        };

        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            var result = response.FirstOrDefault();
            if (result != null)
            {
                summary.TotalMembers = result.TotalMembers;
                summary.ScoredMembers = result.ScoredMembers;
                summary.SubmittedMembers = result.SubmittedMembers;
                summary.AverageRiskScore = result.AverageRiskScore;
                summary.MinRiskScore = result.MinRiskScore;
                summary.MaxRiskScore = result.MaxRiskScore;
            }
        }

        return summary;
    }

    /// <summary>Typed projection matching the aggregate SELECT aliases.</summary>
    private sealed record MeasurementYearProjection(
        int TotalMembers,
        int ScoredMembers,
        int SubmittedMembers,
        decimal AverageRiskScore,
        decimal MinRiskScore,
        decimal MaxRiskScore);

    public async Task<MemberRiskScore> CreateAsync(MemberRiskScore score)
    {
        score.TenantId = GetTenantId();
        var response = await _container.CreateItemAsync(score, new PartitionKey(score.Id));
        return response.Resource;
    }

    public async Task<MemberRiskScore> UpdateAsync(MemberRiskScore score)
    {
        score.TenantId = GetTenantId();
        var response = await _container.ReplaceItemAsync(score, score.Id, new PartitionKey(score.Id));
        return response.Resource;
    }

    public async Task DeleteAsync(string id)
    {
        await _container.DeleteItemAsync<MemberRiskScore>(id, new PartitionKey(id));
    }
}
