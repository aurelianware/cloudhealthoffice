using Microsoft.Azure.Cosmos;
using PremiumBillingService.Models;

namespace PremiumBillingService.Repositories;

public interface IPremiumInvoiceRepository
{
    Task<PremiumInvoice?> GetByIdAsync(string id);
    Task<IEnumerable<PremiumInvoice>> GetByGroupNumberAsync(string groupNumber);
    Task<IEnumerable<PremiumInvoice>> GetByBillingPeriodAsync(DateTime billingPeriodStart);
    Task<IEnumerable<PremiumInvoice>> GetByStatusAsync(InvoiceStatus status);
    Task<IEnumerable<PremiumInvoice>> SearchAsync(
        string? groupNumber = null,
        DateTime? periodFrom = null,
        DateTime? periodTo = null,
        InvoiceStatus? status = null,
        int page = 1,
        int pageSize = 50);
    Task<IEnumerable<PremiumInvoice>> GetOverdueAsync();

    /// <summary>
    /// Return invoices whose <c>LineItems</c> include at least one line for
    /// the given <paramref name="memberId"/>, newest first. Limited to
    /// <paramref name="take"/> invoices — the portal Member Details Premium
    /// tab shows the last 12 billing periods.
    /// </summary>
    Task<IEnumerable<PremiumInvoice>> ListByMemberAsync(string memberId, int take = 12);

    Task<PremiumInvoice> CreateAsync(PremiumInvoice invoice);
    Task<PremiumInvoice> UpdateAsync(PremiumInvoice invoice);
    Task DeleteAsync(string id);
}

public class PremiumInvoiceRepository : IPremiumInvoiceRepository
{
    private readonly Container _container;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<PremiumInvoiceRepository> _logger;

    public PremiumInvoiceRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<PremiumInvoiceRepository> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        _container = cosmosClient.GetContainer(databaseName, "PremiumInvoices");
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

    public async Task<PremiumInvoice?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        try
        {
            var response = await _container.ReadItemAsync<PremiumInvoice>(id, new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IEnumerable<PremiumInvoice>> GetByGroupNumberAsync(string groupNumber)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.groupNumber = @groupNumber ORDER BY c.billingPeriodStart DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@groupNumber", groupNumber);

        return await ExecuteQueryAsync(query);
    }

    public async Task<IEnumerable<PremiumInvoice>> GetByBillingPeriodAsync(DateTime billingPeriodStart)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.billingPeriodStart = @period ORDER BY c.groupNumber")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@period", billingPeriodStart);

        return await ExecuteQueryAsync(query);
    }

    public async Task<IEnumerable<PremiumInvoice>> GetByStatusAsync(InvoiceStatus status)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.status = @status ORDER BY c.dueDate")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@status", (int)status);

        return await ExecuteQueryAsync(query);
    }

    public async Task<IEnumerable<PremiumInvoice>> SearchAsync(
        string? groupNumber = null,
        DateTime? periodFrom = null,
        DateTime? periodTo = null,
        InvoiceStatus? status = null,
        int page = 1,
        int pageSize = 50)
    {
        var tenantId = GetTenantId();
        var queryText = "SELECT * FROM c WHERE c.tenantId = @tenantId";
        var parameters = new List<(string, object)> { ("@tenantId", tenantId) };

        if (!string.IsNullOrEmpty(groupNumber))
        {
            queryText += " AND c.groupNumber = @groupNumber";
            parameters.Add(("@groupNumber", groupNumber));
        }
        if (periodFrom.HasValue)
        {
            queryText += " AND c.billingPeriodStart >= @periodFrom";
            parameters.Add(("@periodFrom", periodFrom.Value));
        }
        if (periodTo.HasValue)
        {
            queryText += " AND c.billingPeriodStart <= @periodTo";
            parameters.Add(("@periodTo", periodTo.Value));
        }
        if (status.HasValue)
        {
            queryText += " AND c.status = @status";
            parameters.Add(("@status", (int)status.Value));
        }

        queryText += " ORDER BY c.billingPeriodStart DESC";
        queryText += $" OFFSET {(page - 1) * pageSize} LIMIT {pageSize}";

        var queryDefinition = new QueryDefinition(queryText);
        foreach (var param in parameters)
            queryDefinition = queryDefinition.WithParameter(param.Item1, param.Item2);

        return await ExecuteQueryAsync(queryDefinition);
    }

    public async Task<IEnumerable<PremiumInvoice>> GetOverdueAsync()
    {
        var tenantId = GetTenantId();
        var now = DateTime.UtcNow;

        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.balanceDue > 0 AND c.dueDate < @now AND c.status != @voided AND c.status != @writeOff AND c.status != @paid ORDER BY c.dueDate")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@now", now)
            .WithParameter("@voided", (int)InvoiceStatus.Voided)
            .WithParameter("@writeOff", (int)InvoiceStatus.WriteOff)
            .WithParameter("@paid", (int)InvoiceStatus.Paid);

        return await ExecuteQueryAsync(query);
    }

    public async Task<IEnumerable<PremiumInvoice>> ListByMemberAsync(string memberId, int take = 12)
    {
        var tenantId = GetTenantId();
        // EXISTS over a JOINed sub-query into LineItems to keep the predicate
        // on the indexed (tenantId, memberId) path. Cosmos can't index inside
        // arrays without explicit composite indexes, so pull recent invoices
        // for the tenant and filter to those that mention the member.
        var query = new QueryDefinition(
            "SELECT TOP @take * FROM c " +
            "WHERE c.tenantId = @tenantId " +
            "AND EXISTS(SELECT VALUE l FROM l IN c.lineItems WHERE l.memberId = @memberId) " +
            "ORDER BY c.billingPeriodStart DESC")
            .WithParameter("@take", take)
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@memberId", memberId);

        return await ExecuteQueryAsync(query);
    }

    public async Task<PremiumInvoice> CreateAsync(PremiumInvoice invoice)
    {
        invoice.TenantId = GetTenantId();
        invoice.CreatedAt = DateTime.UtcNow;
        invoice.LastUpdatedAt = DateTime.UtcNow;
        var response = await _container.CreateItemAsync(invoice, new PartitionKey(invoice.TenantId));
        _logger.LogInformation("Created premium invoice {InvoiceNumber} for group {GroupNumber}",
            invoice.InvoiceNumber, invoice.GroupNumber);
        return response.Resource;
    }

    public async Task<PremiumInvoice> UpdateAsync(PremiumInvoice invoice)
    {
        invoice.LastUpdatedAt = DateTime.UtcNow;
        var response = await _container.ReplaceItemAsync(invoice, invoice.Id, new PartitionKey(invoice.TenantId));
        _logger.LogInformation("Updated premium invoice {InvoiceNumber}", invoice.InvoiceNumber);
        return response.Resource;
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();
        await _container.DeleteItemAsync<PremiumInvoice>(id, new PartitionKey(tenantId));
        _logger.LogInformation("Deleted premium invoice {Id}", SanitizeForLog(id));
    }

    private async Task<List<PremiumInvoice>> ExecuteQueryAsync(QueryDefinition query)
    {
        var iterator = _container.GetItemQueryIterator<PremiumInvoice>(query);
        var results = new List<PremiumInvoice>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }
        return results;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
