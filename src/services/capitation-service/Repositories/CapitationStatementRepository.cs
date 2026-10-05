using Microsoft.Azure.Cosmos;
using CapitationService.Models;

namespace CapitationService.Repositories;

public interface ICapitationStatementRepository
{
    Task<CapitationStatement?> GetByIdAsync(string id);
    Task<IEnumerable<CapitationStatement>> GetByRunIdAsync(string runId);
    Task<IEnumerable<CapitationStatement>> GetByProviderNpiAsync(string npi, DateTime? periodFrom = null, DateTime? periodTo = null);
    Task<IEnumerable<CapitationStatement>> GetByStatusAsync(CapitationStatementStatus status);
    Task<IEnumerable<CapitationStatement>> GetUnpaidStatementsAsync();
    Task<CapitationStatement> CreateAsync(CapitationStatement statement);
    Task<CapitationStatement> UpdateAsync(CapitationStatement statement);

    /// <summary>
    /// Approved to PaymentInitiated naming <paramref name="disbursementId"/>, as one
    /// conditional write before any money moves: of two releases of the same
    /// statement exactly one gets it. False when it is no longer Approved.
    /// </summary>
    Task<bool> TryStartPaymentAsync(string statementId, string disbursementId);

    /// <summary>
    /// PaymentInitiated by <paramref name="disbursementId"/> back to Approved: that
    /// payment did not go out. Does nothing otherwise.
    /// </summary>
    Task UndoStartPaymentAsync(string statementId, string disbursementId);

    /// <summary>
    /// PaymentInitiated by <paramref name="disbursementId"/> to PaymentUnknown:
    /// the payment may have gone out, so the statement is not payable until
    /// someone checks. False when it is not in that state.
    /// </summary>
    Task<bool> MarkPaymentUnknownAsync(string statementId, string disbursementId);

    /// <summary>
    /// PaymentUnknown by <paramref name="disbursementId"/>, once someone checked:
    /// paid goes to PaymentInitiated (settled later); not paid goes back to
    /// Approved (payable again). False when it is not in that state.
    /// </summary>
    Task<bool> ResolvePaymentUnknownAsync(string statementId, string disbursementId, bool paid);
}

public class CapitationStatementRepository : ICapitationStatementRepository
{
    private readonly Container _container;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<CapitationStatementRepository> _logger;

    public CapitationStatementRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<CapitationStatementRepository> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        _container = cosmosClient.GetContainer(databaseName, "CapitationStatements");
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

    public async Task<CapitationStatement?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        try
        {
            var response = await _container.ReadItemAsync<CapitationStatement>(id, new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IEnumerable<CapitationStatement>> GetByRunIdAsync(string runId)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.capitationRunId = @runId ORDER BY c.providerName")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@runId", runId);

        return await ExecuteQueryAsync(query);
    }

    public async Task<IEnumerable<CapitationStatement>> GetByProviderNpiAsync(string npi, DateTime? periodFrom = null, DateTime? periodTo = null)
    {
        var tenantId = GetTenantId();
        var queryText = "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.providerNPI = @npi";
        var parameters = new List<(string, object)>
        {
            ("@tenantId", tenantId),
            ("@npi", npi)
        };

        if (periodFrom.HasValue)
        {
            queryText += " AND c.capitationPeriodStart >= @periodFrom";
            parameters.Add(("@periodFrom", periodFrom.Value));
        }
        if (periodTo.HasValue)
        {
            queryText += " AND c.capitationPeriodStart <= @periodTo";
            parameters.Add(("@periodTo", periodTo.Value));
        }

        queryText += " ORDER BY c.capitationPeriodStart DESC";

        var queryDefinition = new QueryDefinition(queryText);
        foreach (var param in parameters)
            queryDefinition = queryDefinition.WithParameter(param.Item1, param.Item2);

        return await ExecuteQueryAsync(queryDefinition);
    }

    public async Task<IEnumerable<CapitationStatement>> GetByStatusAsync(CapitationStatementStatus status)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.status = @status ORDER BY c.capitationPeriodStart DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@status", status.ToString());

        return await ExecuteQueryAsync(query);
    }

    public async Task<IEnumerable<CapitationStatement>> GetUnpaidStatementsAsync()
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.status != @paid AND c.status != @voided AND c.netPayable > 0 ORDER BY c.capitationPeriodStart")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@paid", CapitationStatementStatus.Paid.ToString())
            .WithParameter("@voided", CapitationStatementStatus.Voided.ToString());

        return await ExecuteQueryAsync(query);
    }

    public async Task<CapitationStatement> CreateAsync(CapitationStatement statement)
    {
        statement.TenantId = GetTenantId();
        statement.CreatedAt = DateTime.UtcNow;
        statement.LastUpdatedAt = DateTime.UtcNow;
        var response = await _container.CreateItemAsync(statement, new PartitionKey(statement.TenantId));
        _logger.LogInformation("Created capitation statement {StatementNumber} for provider {NPI}",
            statement.StatementNumber, statement.ProviderNPI);
        return response.Resource;
    }

    public Task<bool> TryStartPaymentAsync(string statementId, string disbursementId)
        => TryTransitionAsync(statementId,
            s => s.Status == CapitationStatementStatus.Approved,
            s => { s.Status = CapitationStatementStatus.PaymentInitiated; s.EftDisbursementId = disbursementId; });

    public async Task UndoStartPaymentAsync(string statementId, string disbursementId)
        => await TryTransitionAsync(statementId,
            s => s.Status == CapitationStatementStatus.PaymentInitiated && s.EftDisbursementId == disbursementId,
            s => { s.Status = CapitationStatementStatus.Approved; s.EftDisbursementId = null; },
            attempts: 5);

    public Task<bool> MarkPaymentUnknownAsync(string statementId, string disbursementId)
        => TryTransitionAsync(statementId,
            s => s.Status == CapitationStatementStatus.PaymentInitiated && s.EftDisbursementId == disbursementId,
            s => s.Status = CapitationStatementStatus.PaymentUnknown,
            attempts: 5);

    public Task<bool> ResolvePaymentUnknownAsync(string statementId, string disbursementId, bool paid)
        => TryTransitionAsync(statementId,
            s => s.Status == CapitationStatementStatus.PaymentUnknown && s.EftDisbursementId == disbursementId,
            s =>
            {
                s.Status = paid ? CapitationStatementStatus.PaymentInitiated : CapitationStatementStatus.Approved;
                if (!paid) s.EftDisbursementId = null;
            },
            attempts: 5);

    /// <summary>Read, check, replace only the version read (ETag). False when the check fails.</summary>
    private async Task<bool> TryTransitionAsync(
        string statementId, Func<CapitationStatement, bool> allowed, Action<CapitationStatement> apply, int attempts = 1)
    {
        var tenantId = GetTenantId();
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            ItemResponse<CapitationStatement> current;
            try
            {
                current = await _container.ReadItemAsync<CapitationStatement>(statementId, new PartitionKey(tenantId));
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return false;
            }

            var statement = current.Resource;
            if (!allowed(statement))
                return false;
            apply(statement);
            statement.LastUpdatedAt = DateTime.UtcNow;
            try
            {
                await _container.ReplaceItemAsync(statement, statementId, new PartitionKey(tenantId),
                    new ItemRequestOptions { IfMatchEtag = current.ETag });
                return true;
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
            {
                // Changed since the read: the check is made again on the next read.
            }
        }
        return false;
    }

    public async Task<CapitationStatement> UpdateAsync(CapitationStatement statement)
    {
        statement.LastUpdatedAt = DateTime.UtcNow;
        var response = await _container.ReplaceItemAsync(statement, statement.Id, new PartitionKey(statement.TenantId));
        _logger.LogInformation("Updated capitation statement {StatementNumber}", statement.StatementNumber);
        return response.Resource;
    }

    private async Task<List<CapitationStatement>> ExecuteQueryAsync(QueryDefinition query)
    {
        var iterator = _container.GetItemQueryIterator<CapitationStatement>(query);
        var results = new List<CapitationStatement>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }
        return results;
    }
}
