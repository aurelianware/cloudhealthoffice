using Microsoft.Azure.Cosmos;
using AppealsService.Models;

namespace AppealsService.Repositories;

public interface IAppealRepository
{
    Task<Appeal?> GetByIdAsync(string id);
    Task<Appeal?> GetByAppealNumberAsync(string appealNumber);
    Task<IEnumerable<Appeal>> GetByClaimIdAsync(string claimId);
    Task<IEnumerable<Appeal>> SearchAsync(
        string? memberId,
        string? providerNPI,
        DateTime? submittedFrom,
        DateTime? submittedTo,
        AppealStatus? status,
        LineOfBusiness? lineOfBusiness,
        int page = 1,
        int pageSize = 50);
    Task<AppealsSummary> GetAppealsSummaryAsync(DateTime from, DateTime to);
    Task<Appeal> CreateAsync(Appeal appeal);
    Task<Appeal> UpdateAsync(Appeal appeal);
    Task DeleteAsync(string id);
}

public class AppealRepository : IAppealRepository
{
    private readonly Container _container;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AppealRepository> _logger;

    public AppealRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<AppealRepository> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        var containerName = configuration["CosmosDb:ContainerName"] ?? "Appeals";

        _container = cosmosClient.GetContainer(databaseName, containerName);
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    private string GetTenantId()
    {
        var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
        {
            throw new InvalidOperationException("TenantId not found in request context");
        }
        return tenantId;
    }

    public async Task<Appeal?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();

        try
        {
            var response = await _container.ReadItemAsync<Appeal>(
                id,
                new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Appeal?> GetByAppealNumberAsync(string appealNumber)
    {
        var tenantId = GetTenantId();

        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.appealNumber = @appealNumber")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@appealNumber", appealNumber);

        var iterator = _container.GetItemQueryIterator<Appeal>(query);
        var results = new List<Appeal>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results.FirstOrDefault();
    }

    public async Task<IEnumerable<Appeal>> GetByClaimIdAsync(string claimId)
    {
        var tenantId = GetTenantId();

        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.claimId = @claimId ORDER BY c.submittedDate DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@claimId", claimId);

        var iterator = _container.GetItemQueryIterator<Appeal>(query);
        var results = new List<Appeal>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    public async Task<IEnumerable<Appeal>> SearchAsync(
        string? memberId,
        string? providerNPI,
        DateTime? submittedFrom,
        DateTime? submittedTo,
        AppealStatus? status,
        LineOfBusiness? lineOfBusiness,
        int page = 1,
        int pageSize = 50)
    {
        var tenantId = GetTenantId();

        var queryText = "SELECT * FROM c WHERE c.tenantId = @tenantId";
        var parameters = new List<(string, object)> { ("@tenantId", tenantId) };

        if (!string.IsNullOrEmpty(memberId))
        {
            queryText += " AND c.memberId = @memberId";
            parameters.Add(("@memberId", memberId));
        }

        if (!string.IsNullOrEmpty(providerNPI))
        {
            queryText += " AND c.providerNPI = @providerNPI";
            parameters.Add(("@providerNPI", providerNPI));
        }

        if (submittedFrom.HasValue)
        {
            queryText += " AND c.submittedDate >= @submittedFrom";
            parameters.Add(("@submittedFrom", submittedFrom.Value));
        }

        if (submittedTo.HasValue)
        {
            queryText += " AND c.submittedDate <= @submittedTo";
            parameters.Add(("@submittedTo", submittedTo.Value));
        }

        if (status.HasValue)
        {
            queryText += " AND c.status = @status";
            parameters.Add(("@status", (int)status.Value));
        }

        if (lineOfBusiness.HasValue)
        {
            queryText += " AND c.lineOfBusiness = @lineOfBusiness";
            parameters.Add(("@lineOfBusiness", (int)lineOfBusiness.Value));
        }

        queryText += " ORDER BY c.submittedDate DESC";
        queryText += $" OFFSET {(page - 1) * pageSize} LIMIT {pageSize}";

        var queryDefinition = new QueryDefinition(queryText);
        foreach (var param in parameters)
        {
            queryDefinition = queryDefinition.WithParameter(param.Item1, param.Item2);
        }

        var iterator = _container.GetItemQueryIterator<Appeal>(queryDefinition);
        var results = new List<Appeal>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    public async Task<AppealsSummary> GetAppealsSummaryAsync(DateTime from, DateTime to)
    {
        var tenantId = GetTenantId();

        var appeals = await SearchAsync(null, null, from, to, null, null, 1, 10000);
        var appealsList = appeals.ToList();

        var summary = new AppealsSummary
        {
            TotalAppeals = appealsList.Count,
            InReview = appealsList.Count(a => a.Status == AppealStatus.InReview),
            Approved = appealsList.Count(a => a.Status == AppealStatus.Approved),
            Denied = appealsList.Count(a => a.Status == AppealStatus.Denied),
            PartialApprovals = appealsList.Count(a => a.Status == AppealStatus.PartialApproval),
            TotalAppealedAmount = appealsList.Sum(a => a.AppealedAmount),
            TotalApprovedAmount = appealsList
                .Where(a => a.Decision != null && a.Decision.ApprovedAmount.HasValue)
                .Sum(a => a.Decision!.ApprovedAmount!.Value)
        };

        // Calculate average decision time
        var decidedAppeals = appealsList.Where(a => a.DecisionDate.HasValue).ToList();
        if (decidedAppeals.Any())
        {
            summary.AverageDecisionTimeDays = decidedAppeals
                .Average(a => (a.DecisionDate!.Value - a.SubmittedDate).TotalDays);
        }

        // Calculate approval rate
        var totalDecided = appealsList.Count(a => a.Status == AppealStatus.Approved || 
                                                  a.Status == AppealStatus.Denied || 
                                                  a.Status == AppealStatus.PartialApproval);
        if (totalDecided > 0)
        {
            summary.ApprovalRate = ((double)(summary.Approved + summary.PartialApprovals) / totalDecided) * 100;
        }

        // Group by status
        foreach (var appeal in appealsList)
        {
            if (!summary.AppealsByStatus.ContainsKey(appeal.Status))
                summary.AppealsByStatus[appeal.Status] = 0;
            summary.AppealsByStatus[appeal.Status]++;

            if (!summary.AppealsByLevel.ContainsKey(appeal.AppealLevel))
                summary.AppealsByLevel[appeal.AppealLevel] = 0;
            summary.AppealsByLevel[appeal.AppealLevel]++;
        }

        return summary;
    }

    public async Task<Appeal> CreateAsync(Appeal appeal)
    {
        appeal.TenantId = GetTenantId();
        appeal.SubmittedDate = DateTime.UtcNow;

        // Calculate target response date (typically 30-60 days)
        appeal.TargetResponseDate = appeal.SubmittedDate.AddDays(appeal.IsUrgent ? 30 : 60);

        var response = await _container.CreateItemAsync(
            appeal,
            new PartitionKey(appeal.TenantId));

        _logger.LogInformation("Created appeal {AppealId} for claim {ClaimId}",
            appeal.Id, appeal.ClaimId);

        return response.Resource;
    }

    public async Task<Appeal> UpdateAsync(Appeal appeal)
    {
        var response = await _container.ReplaceItemAsync(
            appeal,
            appeal.Id,
            new PartitionKey(appeal.TenantId));

        _logger.LogInformation("Updated appeal {AppealId}", appeal.Id);

        return response.Resource;
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();

        await _container.DeleteItemAsync<Appeal>(
            id,
            new PartitionKey(tenantId));

        _logger.LogInformation("Deleted appeal {AppealId}", id);
    }
}
