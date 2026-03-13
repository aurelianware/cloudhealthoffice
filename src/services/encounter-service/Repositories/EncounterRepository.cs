using Microsoft.Azure.Cosmos;
using EncounterService.Models;

namespace EncounterService.Repositories;

public interface IEncounterRepository
{
    Task<Encounter?> GetByIdAsync(string id);
    Task<Encounter?> GetByControlNumberAsync(string controlNumber);
    Task<IEnumerable<Encounter>> SearchAsync(
        string? memberId,
        string? payerId,
        string? batchId,
        DateTime? serviceDateFrom,
        DateTime? serviceDateTo,
        EncounterStatus? status,
        SubmissionType? submissionType,
        LineOfBusiness? lineOfBusiness,
        int page,
        int pageSize);
    Task<IEnumerable<Encounter>> GetPendingByPayerAsync(
        string payerId,
        LineOfBusiness? lineOfBusiness,
        EncounterType? encounterType,
        int maxCount);
    Task<EncounterSummary> GetSummaryAsync(DateTime from, DateTime to, string? payerId);
    Task<Encounter> CreateAsync(Encounter encounter);
    Task<Encounter> UpdateAsync(Encounter encounter);
    Task DeleteAsync(string id);
}

public class EncounterRepository : IEncounterRepository
{
    private readonly Container _container;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<EncounterRepository> _logger;

    public EncounterRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<EncounterRepository> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "EncountersDB";
        var containerName = configuration["CosmosDb:ContainerName"] ?? "Encounters";

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

    public async Task<Encounter?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        try
        {
            var response = await _container.ReadItemAsync<Encounter>(id, new PartitionKey(id));
            if (response.Resource.TenantId != tenantId)
                return null;
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Encounter?> GetByControlNumberAsync(string controlNumber)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.encounterControlNumber = @controlNumber")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@controlNumber", controlNumber);

        var iterator = _container.GetItemQueryIterator<Encounter>(query);
        var results = new List<Encounter>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }
        return results.FirstOrDefault();
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
        var conditions = new List<string> { "c.tenantId = @tenantId" };
        var parameters = new Dictionary<string, object> { { "@tenantId", tenantId } };

        if (!string.IsNullOrEmpty(memberId))
        {
            conditions.Add("c.memberId = @memberId");
            parameters["@memberId"] = memberId;
        }
        if (!string.IsNullOrEmpty(payerId))
        {
            conditions.Add("c.payerId = @payerId");
            parameters["@payerId"] = payerId;
        }
        if (!string.IsNullOrEmpty(batchId))
        {
            conditions.Add("c.batchId = @batchId");
            parameters["@batchId"] = batchId;
        }
        if (serviceDateFrom.HasValue)
        {
            conditions.Add("c.serviceDateFrom >= @serviceDateFrom");
            parameters["@serviceDateFrom"] = serviceDateFrom.Value;
        }
        if (serviceDateTo.HasValue)
        {
            conditions.Add("c.serviceDateTo <= @serviceDateTo");
            parameters["@serviceDateTo"] = serviceDateTo.Value;
        }
        if (status.HasValue)
        {
            conditions.Add("c.status = @status");
            parameters["@status"] = status.Value.ToString();
        }
        if (submissionType.HasValue)
        {
            conditions.Add("c.submissionType = @submissionType");
            parameters["@submissionType"] = submissionType.Value.ToString();
        }
        if (lineOfBusiness.HasValue)
        {
            conditions.Add("c.lineOfBusiness = @lineOfBusiness");
            parameters["@lineOfBusiness"] = lineOfBusiness.Value.ToString();
        }

        var queryText = $@"
            SELECT * FROM c
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY c.createdDate DESC
            OFFSET {(page - 1) * pageSize} LIMIT {pageSize}";

        var queryDef = new QueryDefinition(queryText);
        foreach (var (key, value) in parameters)
            queryDef.WithParameter(key, value);

        var iterator = _container.GetItemQueryIterator<Encounter>(queryDef);
        var results = new List<Encounter>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }
        return results;
    }

    public async Task<IEnumerable<Encounter>> GetPendingByPayerAsync(
        string payerId,
        LineOfBusiness? lineOfBusiness,
        EncounterType? encounterType,
        int maxCount)
    {
        var tenantId = GetTenantId();
        var conditions = new List<string>
        {
            "c.tenantId = @tenantId",
            "c.payerId = @payerId",
            "c.status = 'Pending'"
        };
        var parameters = new Dictionary<string, object>
        {
            { "@tenantId", tenantId },
            { "@payerId", payerId }
        };

        if (lineOfBusiness.HasValue)
        {
            conditions.Add("c.lineOfBusiness = @lineOfBusiness");
            parameters["@lineOfBusiness"] = lineOfBusiness.Value.ToString();
        }
        if (encounterType.HasValue)
        {
            conditions.Add("c.encounterType = @encounterType");
            parameters["@encounterType"] = encounterType.Value.ToString();
        }

        var queryText = $@"
            SELECT * FROM c
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY c.createdDate ASC
            OFFSET 0 LIMIT {maxCount}";

        var queryDef = new QueryDefinition(queryText);
        foreach (var (key, value) in parameters)
            queryDef.WithParameter(key, value);

        var iterator = _container.GetItemQueryIterator<Encounter>(queryDef);
        var results = new List<Encounter>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }
        return results;
    }

    public async Task<EncounterSummary> GetSummaryAsync(DateTime from, DateTime to, string? payerId)
    {
        var tenantId = GetTenantId();
        var payerCondition = !string.IsNullOrEmpty(payerId)
            ? "AND c.payerId = @payerId"
            : "";

        var queryText = $@"
            SELECT
                COUNT(1) as TotalEncounters,
                SUM(CASE WHEN c.status = 'Pending' THEN 1 ELSE 0 END) as PendingEncounters,
                SUM(CASE WHEN c.status = 'Queued' THEN 1 ELSE 0 END) as QueuedEncounters,
                SUM(CASE WHEN c.status = 'Submitted' THEN 1 ELSE 0 END) as SubmittedEncounters,
                SUM(CASE WHEN c.status = 'Accepted' THEN 1 ELSE 0 END) as AcceptedEncounters,
                SUM(CASE WHEN c.status = 'Rejected' THEN 1 ELSE 0 END) as RejectedEncounters,
                SUM(CASE WHEN c.submissionType = 'Correction' THEN 1 ELSE 0 END) as CorrectionEncounters,
                SUM(c.totalChargeAmount) as TotalChargeAmount
            FROM c
            WHERE c.tenantId = @tenantId
            AND c.createdDate >= @from
            AND c.createdDate <= @to
            {payerCondition}";

        var queryDef = new QueryDefinition(queryText)
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@from", from)
            .WithParameter("@to", to);

        if (!string.IsNullOrEmpty(payerId))
            queryDef.WithParameter("@payerId", payerId);

        var iterator = _container.GetItemQueryIterator<EncounterSummaryProjection>(queryDef);
        var summary = new EncounterSummary();

        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            var result = response.FirstOrDefault();
            if (result != null)
            {
                summary.TotalEncounters = result.TotalEncounters;
                summary.PendingEncounters = result.PendingEncounters;
                summary.QueuedEncounters = result.QueuedEncounters;
                summary.SubmittedEncounters = result.SubmittedEncounters;
                summary.AcceptedEncounters = result.AcceptedEncounters;
                summary.RejectedEncounters = result.RejectedEncounters;
                summary.CorrectionEncounters = result.CorrectionEncounters;
                summary.TotalChargeAmount = result.TotalChargeAmount;

                if (summary.TotalEncounters > 0)
                {
                    summary.AcceptanceRate =
                        (decimal)summary.AcceptedEncounters / summary.TotalEncounters * 100;
                }
            }
        }

        return summary;
    }

    /// <summary>Typed projection matching the aggregate SELECT aliases.</summary>
    private sealed record EncounterSummaryProjection(
        int TotalEncounters,
        int PendingEncounters,
        int QueuedEncounters,
        int SubmittedEncounters,
        int AcceptedEncounters,
        int RejectedEncounters,
        int CorrectionEncounters,
        decimal TotalChargeAmount);

    public async Task<Encounter> CreateAsync(Encounter encounter)
    {
        var tenantId = GetTenantId();
        encounter.TenantId = tenantId;
        var response = await _container.CreateItemAsync(encounter, new PartitionKey(encounter.Id));
        return response.Resource;
    }

    public async Task<Encounter> UpdateAsync(Encounter encounter)
    {
        var tenantId = GetTenantId();
        encounter.TenantId = tenantId;
        var response = await _container.ReplaceItemAsync(
            encounter, encounter.Id, new PartitionKey(encounter.Id));
        return response.Resource;
    }

    public async Task DeleteAsync(string id)
    {
        await _container.DeleteItemAsync<Encounter>(id, new PartitionKey(id));
    }
}
