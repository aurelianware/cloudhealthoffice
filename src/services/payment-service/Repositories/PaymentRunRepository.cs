using Microsoft.Azure.Cosmos;
using PaymentService.Models;

namespace PaymentService.Repositories;

public interface IPaymentRunRepository
{
    Task<PaymentRun?> GetByIdAsync(string id);
    Task<PaymentRun?> GetByPaymentRunNumberAsync(string paymentRunNumber);
    Task<IEnumerable<PaymentRun>> SearchAsync(DateTime from, DateTime to, PaymentRunStatus? status = null);
    Task<PaymentRun> CreateAsync(PaymentRun paymentRun);
    Task<PaymentRun> UpdateAsync(PaymentRun paymentRun);

    /// <summary>
    /// Atomically moves the run from Pending to Running with its executor and
    /// start time (one conditional write). False when the run is not Pending
    /// any more (another executor won), or does not exist.
    /// </summary>
    Task<bool> TryStartAsync(string id, string executedBy, DateTime startedAt);

    /// <summary>
    /// Records released and needs-attention reservations (and warnings) on the
    /// run as a partial update: never rewrites the run's status or results, so
    /// it cannot undo a run that finished meanwhile. False when the run is gone.
    /// </summary>
    Task<bool> RecordReservationOutcomesAsync(string id, ReservationOutcomes outcomes);

    /// <summary>
    /// Writes only the run's EFT-file fields: sets <see cref="PaymentRun.EftFile"/>
    /// and appends <paramref name="addFallbacks"/> / <paramref name="addWarnings"/>,
    /// never rewriting anything else on the run (status, payments, results). With
    /// <paramref name="expectedSha256"/> null it applies only while the run has no
    /// EFT file (the first pin); otherwise only while the pinned file's SHA-256 is
    /// <paramref name="expectedSha256"/>. False when the condition did not hold or the run is gone.
    /// </summary>
    Task<bool> TrySaveEftFileAsync(string id, PaymentRunEftFile file, string? expectedSha256,
        IReadOnlyList<CheckFallbackPayment> addFallbacks, IReadOnlyList<string> addWarnings);

    /// <summary>
    /// Replaces the pinned EFT file with a re-dated one and appends the old one
    /// (<paramref name="superseded"/>, with its Superseded* fields set) to
    /// <see cref="PaymentRun.EftFileHistory"/>, only while the pinned file's SHA-256
    /// is still <c>superseded.Sha256</c>. Touches nothing else on the run.
    /// </summary>
    Task<bool> TryRepinEftFileAsync(string id, PaymentRunEftFile file, PaymentRunEftFile superseded);

    Task DeleteAsync(string id);
}

public class PaymentRunRepository : IPaymentRunRepository
{
    private readonly Container _container;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<PaymentRunRepository> _logger;

    public PaymentRunRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<PaymentRunRepository> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        var containerName = "PaymentRuns"; // Separate container for payment runs

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

    public async Task<PaymentRun?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();

        try
        {
            var response = await _container.ReadItemAsync<PaymentRun>(
                id,
                new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<PaymentRun?> GetByPaymentRunNumberAsync(string paymentRunNumber)
    {
        var tenantId = GetTenantId();

        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.paymentRunNumber = @paymentRunNumber")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@paymentRunNumber", paymentRunNumber);

        var iterator = _container.GetItemQueryIterator<PaymentRun>(query);
        var results = new List<PaymentRun>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results.FirstOrDefault();
    }

    public async Task<IEnumerable<PaymentRun>> SearchAsync(DateTime from, DateTime to, PaymentRunStatus? status = null)
    {
        var tenantId = GetTenantId();

        var queryText = "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.createdAt >= @from AND c.createdAt <= @to";
        var parameters = new List<(string, object)>
        {
            ("@tenantId", tenantId),
            ("@from", from),
            ("@to", to)
        };

        if (status.HasValue)
        {
            queryText += " AND c.status = @status";
            parameters.Add(("@status", (int)status.Value));
        }

        queryText += " ORDER BY c.createdAt DESC";

        var queryDefinition = new QueryDefinition(queryText);
        foreach (var param in parameters)
        {
            queryDefinition = queryDefinition.WithParameter(param.Item1, param.Item2);
        }

        var iterator = _container.GetItemQueryIterator<PaymentRun>(queryDefinition);
        var results = new List<PaymentRun>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    public async Task<PaymentRun> CreateAsync(PaymentRun paymentRun)
    {
        paymentRun.TenantId = GetTenantId();
        paymentRun.CreatedAt = DateTime.UtcNow;

        var response = await _container.CreateItemAsync(
            paymentRun,
            new PartitionKey(paymentRun.TenantId));

        _logger.LogInformation("Created payment run {PaymentRunNumber}", paymentRun.PaymentRunNumber);

        return response.Resource;
    }

    public async Task<bool> TryStartAsync(string id, string executedBy, DateTime startedAt)
    {
        var tenantId = GetTenantId();
        ItemResponse<PaymentRun> current;
        try
        {
            current = await _container.ReadItemAsync<PaymentRun>(id, new PartitionKey(tenantId));
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        var run = current.Resource;
        if (run.Status != PaymentRunStatus.Pending)
            return false;

        run.Status = PaymentRunStatus.Running;
        run.ExecutedBy = executedBy;
        run.ExecutionStartedAt = startedAt;
        try
        {
            // Optimistic concurrency: the replace only applies to the version
            // read above, so of two executors exactly one moves it to Running.
            await _container.ReplaceItemAsync(run, id, new PartitionKey(tenantId),
                new ItemRequestOptions { IfMatchEtag = current.ETag });
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }

    public async Task<bool> RecordReservationOutcomesAsync(string id, ReservationOutcomes outcomes)
    {
        var tenantId = GetTenantId();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            ItemResponse<PaymentRun> current;
            try
            {
                current = await _container.ReadItemAsync<PaymentRun>(id, new PartitionKey(tenantId));
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return false;
            }

            var run = current.Resource;
            outcomes.ApplyTo(run.ReleasedReservationClaimIds, run.ReservationsNeedingAttention, run.Warnings);
            try
            {
                // Only the version just read: a concurrent write (the run
                // finishing) is re-read and kept, never overwritten.
                await _container.ReplaceItemAsync(run, id, new PartitionKey(tenantId),
                    new ItemRequestOptions { IfMatchEtag = current.ETag });
                return true;
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
            {
                // Changed since read: try again on the new version.
            }
        }
        throw new InvalidOperationException($"Payment run {id} kept changing; reservation outcomes not recorded");
    }

    public async Task<bool> TrySaveEftFileAsync(string id, PaymentRunEftFile file, string? expectedSha256,
        IReadOnlyList<CheckFallbackPayment> addFallbacks, IReadOnlyList<string> addWarnings)
    {
        var tenantId = GetTenantId();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            ItemResponse<PaymentRun> current;
            try
            {
                current = await _container.ReadItemAsync<PaymentRun>(id, new PartitionKey(tenantId));
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return false;
            }

            var run = current.Resource;
            var holds = expectedSha256 == null
                ? run.EftFile == null
                : run.EftFile != null && string.Equals(run.EftFile.Sha256, expectedSha256, StringComparison.Ordinal);
            if (!holds)
                return false;

            run.EftFile = file;
            run.CheckFallbacks.AddRange(addFallbacks.Where(f => run.CheckFallbacks.All(x => x.PaymentId != f.PaymentId)));
            run.Warnings.AddRange(addWarnings);
            try
            {
                // Only the version just read: anything written since is re-read and kept.
                await _container.ReplaceItemAsync(run, id, new PartitionKey(tenantId),
                    new ItemRequestOptions { IfMatchEtag = current.ETag });
                return true;
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
            {
                // Changed since read: re-check the condition on the new version.
            }
        }
        throw new InvalidOperationException($"Payment run {id} kept changing; its EFT file was not recorded");
    }

    public async Task<bool> TryRepinEftFileAsync(string id, PaymentRunEftFile file, PaymentRunEftFile superseded)
    {
        var tenantId = GetTenantId();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            ItemResponse<PaymentRun> current;
            try
            {
                current = await _container.ReadItemAsync<PaymentRun>(id, new PartitionKey(tenantId));
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return false;
            }

            var run = current.Resource;
            if (run.EftFile == null || !string.Equals(run.EftFile.Sha256, superseded.Sha256, StringComparison.Ordinal))
                return false;
            run.EftFile = file;
            run.EftFileHistory.Add(superseded);
            try
            {
                await _container.ReplaceItemAsync(run, id, new PartitionKey(tenantId),
                    new ItemRequestOptions { IfMatchEtag = current.ETag });
                return true;
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
            {
                // Changed since read: re-check on the new version.
            }
        }
        throw new InvalidOperationException($"Payment run {id} kept changing; its EFT file was not re-dated");
    }

    public async Task<PaymentRun> UpdateAsync(PaymentRun paymentRun)
    {
        var response = await _container.ReplaceItemAsync(
            paymentRun,
            paymentRun.Id,
            new PartitionKey(paymentRun.TenantId));

        _logger.LogInformation("Updated payment run {PaymentRunNumber}", paymentRun.PaymentRunNumber);

        return response.Resource;
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();

        await _container.DeleteItemAsync<PaymentRun>(
            id,
            new PartitionKey(tenantId));

        _logger.LogInformation("Deleted payment run {Id}", SanitizeForLog(id));
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
