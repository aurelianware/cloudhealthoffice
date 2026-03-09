using CloudHealthOffice.FeeScheduleEngine.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.FeeScheduleEngine.Persistence;

/// <summary>
/// Cosmos DB implementations of IFeeScheduleRepository and IProviderContractRepository.
///
/// Container layout:
///   fee-schedules     — partition key: /tenantId
///   provider-contracts — partition key: /tenantId
///
/// FeeSchedule lines are embedded in the parent document (Cosmos document-per-schedule).
/// Individual line lookup uses a LINQ-style point read when the caller already has the
/// scheduleId, falling back to a query only for GetDefaultForPlanAsync.
/// </summary>
public class FeeScheduleRepositoryCosmos : IFeeScheduleRepository, IProviderContractRepository
{
    private readonly Container _scheduleContainer;
    private readonly Container _contractContainer;
    private readonly ILogger<FeeScheduleRepositoryCosmos> _logger;

    public FeeScheduleRepositoryCosmos(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        ILogger<FeeScheduleRepositoryCosmos> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        var scheduleContainerName = configuration["FeeScheduleEngine:FeeScheduleContainer"] ?? "FeeSchedules";
        var contractContainerName = configuration["FeeScheduleEngine:ProviderContractContainer"] ?? "ProviderContracts";

        _scheduleContainer = cosmosClient.GetContainer(databaseName, scheduleContainerName);
        _contractContainer = cosmosClient.GetContainer(databaseName, contractContainerName);
        _logger = logger;
    }

    // ── IFeeScheduleRepository ──────────────────────────────────────────

    public async Task<FeeSchedule?> GetByIdAsync(
        string tenantId, string id, CancellationToken ct = default)
    {
        try
        {
            var response = await _scheduleContainer.ReadItemAsync<FeeSchedule>(
                id, new PartitionKey(tenantId), cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<FeeSchedule?> GetDefaultForPlanAsync(
        string tenantId, string planId, DateTime serviceDate, CancellationToken ct = default)
    {
        // A plan's "default" schedule is stored as a tag in a separate config document
        // or queried via planId field. For simplicity, the schedule document embeds the
        // planId it is the default for and we query by effective date range.
        var query = new QueryDefinition(
            "SELECT * FROM c " +
            "WHERE c.tenantId = @tenantId " +
            "AND c.defaultForPlanId = @planId " +
            "AND c.effectiveDate <= @serviceDate " +
            "AND (c.termDate = null OR c.termDate > @serviceDate) " +
            "ORDER BY c.effectiveDate DESC " +
            "OFFSET 0 LIMIT 1")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@planId", planId)
            .WithParameter("@serviceDate", serviceDate.ToString("O"));

        var iterator = _scheduleContainer.GetItemQueryIterator<FeeSchedule>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        if (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            return page.FirstOrDefault();
        }

        return null;
    }

    public async Task<FeeScheduleLine?> GetLineAsync(
        string feeScheduleId, string procedureCode, string? modifier, CancellationToken ct = default)
    {
        // Lines are embedded — we must load the parent document.
        // For high-traffic adjudication paths the service layer caches the full FeeSchedule.
        // Split feeScheduleId to recover tenantId (format: "{tenantId}:{name}:{date}").
        var tenantId = ExtractTenantId(feeScheduleId);

        var schedule = await GetByIdAsync(tenantId, feeScheduleId, ct);
        if (schedule is null) return null;

        return ResolveLinePriority(schedule.Lines, procedureCode, modifier);
    }

    public async Task<FeeSchedule> UpsertAsync(FeeSchedule schedule, CancellationToken ct = default)
    {
        schedule.LastUpdatedDate = DateTime.UtcNow;

        var response = await _scheduleContainer.UpsertItemAsync(
            schedule,
            new PartitionKey(schedule.TenantId),
            cancellationToken: ct);

        _logger.LogDebug("Upserted fee schedule {Id}", schedule.Id);
        return response.Resource;
    }

    public async Task<IReadOnlyList<FeeSchedule>> ListAsync(
        string tenantId, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        int offset = (page - 1) * pageSize;

        var query = new QueryDefinition(
            $"SELECT * FROM c WHERE c.tenantId = @tenantId ORDER BY c.effectiveDate DESC OFFSET {offset} LIMIT {pageSize}")
            .WithParameter("@tenantId", tenantId);

        var iterator = _scheduleContainer.GetItemQueryIterator<FeeSchedule>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        var results = new List<FeeSchedule>();
        while (iterator.HasMoreResults)
        {
            var page2 = await iterator.ReadNextAsync(ct);
            results.AddRange(page2);
        }

        return results;
    }

    // ── IProviderContractRepository ────────────────────────────────────

    Task<ProviderContract?> IProviderContractRepository.GetContractAsync(
        string tenantId, string providerNpi, string planId, DateTime serviceDate,
        CancellationToken ct)
        => GetContractAsync(tenantId, providerNpi, null, planId, serviceDate, ct);

    public async Task<ProviderContract?> GetContractAsync(
        string tenantId, string providerNpi, string? groupTin, string planId,
        DateTime serviceDate, CancellationToken ct = default)
    {
        // Try NPI first.
        var contract = await QueryContractAsync(tenantId, providerNpi, planId, serviceDate, ct);
        if (contract is not null) return contract;

        // Fall back to GroupTin.
        if (!string.IsNullOrEmpty(groupTin))
            contract = await QueryContractAsync(tenantId, groupTin, planId, serviceDate, ct);

        return contract;
    }

    public async Task<ProviderContract> UpsertAsync(ProviderContract contract, CancellationToken ct = default)
    {
        contract.LastUpdatedDate = DateTime.UtcNow;

        var response = await _contractContainer.UpsertItemAsync(
            contract,
            new PartitionKey(contract.TenantId),
            cancellationToken: ct);

        _logger.LogDebug("Upserted provider contract {Id}", contract.Id);
        return response.Resource;
    }

    public async Task<IReadOnlyList<ProviderContract>> ListByProviderAsync(
        string tenantId, string providerNpi, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c " +
            "WHERE c.tenantId = @tenantId " +
            "AND (c.providerNpi = @npi OR c.groupTin = @npi) " +
            "ORDER BY c.effectiveDate DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@npi", providerNpi);

        var iterator = _contractContainer.GetItemQueryIterator<ProviderContract>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        var results = new List<ProviderContract>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page);
        }

        return results;
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private async Task<ProviderContract?> QueryContractAsync(
        string tenantId, string npiOrTin, string planId, DateTime serviceDate,
        CancellationToken ct)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c " +
            "WHERE c.tenantId = @tenantId " +
            "AND (c.providerNpi = @npi OR c.groupTin = @npi) " +
            "AND c.planId = @planId " +
            "AND c.effectiveDate <= @serviceDate " +
            "AND (c.termDate = null OR c.termDate > @serviceDate) " +
            "ORDER BY c.effectiveDate DESC " +
            "OFFSET 0 LIMIT 1")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@npi", npiOrTin)
            .WithParameter("@planId", planId)
            .WithParameter("@serviceDate", serviceDate.ToString("O"));

        var iterator = _contractContainer.GetItemQueryIterator<ProviderContract>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        if (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            return page.FirstOrDefault();
        }

        return null;
    }

    /// <summary>
    /// Applies lookup priority: exact modifier match → base rate (null modifier) → null.
    /// </summary>
    private static FeeScheduleLine? ResolveLinePriority(
        List<FeeScheduleLine> lines, string procedureCode, string? modifier)
    {
        FeeScheduleLine? baseRate = null;

        foreach (var line in lines)
        {
            if (!string.Equals(line.ProcedureCode, procedureCode, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(line.Modifier, modifier, StringComparison.OrdinalIgnoreCase))
                return line; // exact match wins immediately

            if (line.Modifier is null)
                baseRate = line; // capture base rate; keep looking for exact match
        }

        return baseRate;
    }

    private static string ExtractTenantId(string feeScheduleId)
    {
        var colonIndex = feeScheduleId.IndexOf(':');
        return colonIndex > 0 ? feeScheduleId[..colonIndex] : feeScheduleId;
    }
}
