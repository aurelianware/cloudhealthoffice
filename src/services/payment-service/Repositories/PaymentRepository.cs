using Microsoft.Azure.Cosmos;
using PaymentService.Models;

namespace PaymentService.Repositories;

public interface IPaymentRepository
{
    Task<Payment?> GetByIdAsync(string id);
    Task<Payment?> GetByCheckNumberAsync(string checkNumber);
    Task<IEnumerable<Payment>> GetByClaimIdAsync(string claimId);
    Task<IEnumerable<Payment>> SearchAsync(
        DateTime? paymentDateFrom,
        DateTime? paymentDateTo,
        string? payerId,
        PaymentStatus? status,
        int page = 1,
        int pageSize = 50);
    Task<PaymentsSummary> GetPaymentsSummaryAsync(DateTime from, DateTime to);
    Task<Payment> CreateAsync(Payment payment);
    Task<Payment> UpdateAsync(Payment payment);
    Task DeleteAsync(string id);
}

public class PaymentRepository : IPaymentRepository
{
    private readonly Container _container;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<PaymentRepository> _logger;

    public PaymentRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<PaymentRepository> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        var containerName = configuration["CosmosDb:ContainerName"] ?? "Payments";

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

    public async Task<Payment?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();

        try
        {
            var response = await _container.ReadItemAsync<Payment>(
                id,
                new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Payment?> GetByCheckNumberAsync(string checkNumber)
    {
        var tenantId = GetTenantId();

        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.checkNumber = @checkNumber")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@checkNumber", checkNumber);

        var iterator = _container.GetItemQueryIterator<Payment>(query);
        var results = new List<Payment>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results.FirstOrDefault();
    }

    public async Task<IEnumerable<Payment>> GetByClaimIdAsync(string claimId)
    {
        var tenantId = GetTenantId();

        var query = new QueryDefinition(@"
            SELECT * FROM c 
            WHERE c.tenantId = @tenantId 
            AND EXISTS(SELECT VALUE cp FROM cp IN c.claimPayments WHERE cp.claimId = @claimId)")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@claimId", claimId);

        var iterator = _container.GetItemQueryIterator<Payment>(query);
        var results = new List<Payment>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    public async Task<IEnumerable<Payment>> SearchAsync(
        DateTime? paymentDateFrom,
        DateTime? paymentDateTo,
        string? payerId,
        PaymentStatus? status,
        int page = 1,
        int pageSize = 50)
    {
        var tenantId = GetTenantId();

        var queryText = "SELECT * FROM c WHERE c.tenantId = @tenantId";
        var parameters = new List<(string, object)> { ("@tenantId", tenantId) };

        if (paymentDateFrom.HasValue)
        {
            queryText += " AND c.paymentDate >= @dateFrom";
            parameters.Add(("@dateFrom", paymentDateFrom.Value));
        }

        if (paymentDateTo.HasValue)
        {
            queryText += " AND c.paymentDate <= @dateTo";
            parameters.Add(("@dateTo", paymentDateTo.Value));
        }

        if (!string.IsNullOrEmpty(payerId))
        {
            queryText += " AND c.payerId = @payerId";
            parameters.Add(("@payerId", payerId));
        }

        if (status.HasValue)
        {
            queryText += " AND c.status = @status";
            parameters.Add(("@status", (int)status.Value));
        }

        queryText += " ORDER BY c.paymentDate DESC";
        queryText += $" OFFSET {(page - 1) * pageSize} LIMIT {pageSize}";

        var queryDefinition = new QueryDefinition(queryText);
        foreach (var param in parameters)
        {
            queryDefinition = queryDefinition.WithParameter(param.Item1, param.Item2);
        }

        var iterator = _container.GetItemQueryIterator<Payment>(queryDefinition);
        var results = new List<Payment>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    public async Task<PaymentsSummary> GetPaymentsSummaryAsync(DateTime from, DateTime to)
    {
        var tenantId = GetTenantId();

        var query = new QueryDefinition(@"
            SELECT 
                COUNT(1) as TotalPayments,
                SUM(c.totalPaymentAmount) as TotalAmount,
                SUM(ARRAY_LENGTH(c.claimPayments)) as TotalClaims
            FROM c 
            WHERE c.tenantId = @tenantId 
            AND c.paymentDate >= @from 
            AND c.paymentDate <= @to")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@from", from)
            .WithParameter("@to", to);

        var iterator = _container.GetItemQueryIterator<dynamic>(query);
        
        var summary = new PaymentsSummary();

        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            var first = response.FirstOrDefault();
            if (first != null)
            {
                summary.TotalPayments = first.TotalPayments ?? 0;
                summary.TotalPaymentAmount = first.TotalAmount ?? 0;
                summary.TotalClaims = first.TotalClaims ?? 0;
            }
        }

        // Get count by status
        var payments = await SearchAsync(from, to, null, null, 1, 10000);
        foreach (var payment in payments)
        {
            var statusKey = payment.Status.ToString();
            if (!summary.ClaimsByStatus.ContainsKey(statusKey))
                summary.ClaimsByStatus[statusKey] = 0;
            summary.ClaimsByStatus[statusKey]++;

            if (payment.Status == PaymentStatus.Posted)
                summary.PostedPayments++;
            else if (payment.Status == PaymentStatus.Exception)
                summary.ExceptionPayments++;
            else if (payment.Status == PaymentStatus.Received || payment.Status == PaymentStatus.Validated)
                summary.UnpostedPayments++;

            if (!summary.PaymentsByPayer.ContainsKey(payment.PayerName))
                summary.PaymentsByPayer[payment.PayerName] = 0;
            summary.PaymentsByPayer[payment.PayerName] += payment.TotalPaymentAmount;
        }

        return summary;
    }

    public async Task<Payment> CreateAsync(Payment payment)
    {
        payment.TenantId = GetTenantId();
        payment.ReceivedAt = DateTime.UtcNow;

        var response = await _container.CreateItemAsync(
            payment,
            new PartitionKey(payment.TenantId));

        _logger.LogInformation("Created payment {PaymentId} for check {CheckNumber}",
            SanitizeForLog(payment.Id), SanitizeForLog(payment.CheckNumber));

        return response.Resource;
    }

    public async Task<Payment> UpdateAsync(Payment payment)
    {
        var response = await _container.ReplaceItemAsync(
            payment,
            payment.Id,
            new PartitionKey(payment.TenantId));

        _logger.LogInformation("Updated payment {PaymentId}", payment.Id);

        return response.Resource;
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();

        await _container.DeleteItemAsync<Payment>(
            id,
            new PartitionKey(tenantId));

        _logger.LogInformation("Deleted payment {PaymentId}", SanitizeForLog(id));
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
