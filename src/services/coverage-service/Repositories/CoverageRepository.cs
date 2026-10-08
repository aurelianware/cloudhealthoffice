using Microsoft.Azure.Cosmos;
using CoverageService.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CoverageService.Repositories;

/// <summary>
/// Cosmos DB repository for Coverage entities.
/// Uses TenantId as partition key for multi-tenant isolation.
/// </summary>
public class CoverageRepository : ICoverageRepository
{
    private readonly Container _container;
    private const string ContainerName = "Coverage";
    private const string PartitionKeyPath = "/tenantId";

    public CoverageRepository(CosmosClient cosmosClient, string databaseName)
    {
        var database = cosmosClient.GetDatabase(databaseName);
        _container = database.GetContainer(ContainerName);
    }

    public async Task<Coverage?> GetByIdAsync(string tenantId, string id)
    {
        try
        {
            var response = await _container.ReadItemAsync<Coverage>(
                id,
                new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<List<Coverage>> GetActiveCoverageByMemberIdAsync(
        string tenantId,
        string memberId,
        DateTime serviceDate,
        string? insuranceLineCode = null)
    {
        // An open-ended coverage is stored with terminationDate: null (the
        // serializer writes nulls), so both "missing" and null mean no end.
        var queryText = @"
            SELECT * FROM c
            WHERE c.tenantId = @tenantId
            AND c.memberId = @memberId
            AND ARRAY_CONTAINS(@dosStatuses, c.status)
            AND c.effectiveDate <= @serviceDate
            AND (((NOT IS_DEFINED(c.terminationDate) OR IS_NULL(c.terminationDate)) AND c.status != @terminatedStatus)
                 OR c.terminationDate >= @serviceDate)";

        var parameters = new List<(string Name, object Value)>
        {
            ("@tenantId", tenantId),
            ("@memberId", memberId),
            // Open-ended (no termination date) only for non-Terminated coverage:
            // a Terminated record with no termination date fails closed.
            ("@terminatedStatus", (int)CoverageStatus.Terminated),
            // In force on DOS is decided by the date span, not current status
            // (see Coverage.DateOfServiceStatuses).
            ("@dosStatuses", Coverage.DateOfServiceStatuses.Select(s => (int)s).ToArray()),
            ("@serviceDate", serviceDate.Date)
        };

        if (!string.IsNullOrEmpty(insuranceLineCode))
        {
            queryText += " AND c.insuranceLineCode = @insuranceLineCode";
            parameters.Add(("@insuranceLineCode", insuranceLineCode));
        }

        // Built only after every clause is appended: QueryDefinition copies
        // the text, so a clause added afterwards would be silently dropped.
        var queryDef = new QueryDefinition(queryText);
        foreach (var (name, value) in parameters)
        {
            queryDef.WithParameter(name, value);
        }

        var iterator = _container.GetItemQueryIterator<Coverage>(
            queryDef,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(tenantId)
            });

        var results = new List<Coverage>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    public async Task<List<Coverage>> GetCoverageHistoryAsync(
        string tenantId,
        string memberId,
        bool includeTerminated = true)
    {
        var queryText = "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.memberId = @memberId";

        if (!includeTerminated)
        {
            // Terminated = status Terminated, or a termination date already
            // reached that the daily status sweep has not flipped yet.
            queryText += " AND c.status != @terminatedStatus" + NotTerminatedAsOfTodayClause;
        }

        queryText += " ORDER BY c.effectiveDate DESC";

        var queryDef = new QueryDefinition(queryText)
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@memberId", memberId);

        if (!includeTerminated)
        {
            queryDef.WithParameter("@terminatedStatus", (int)CoverageStatus.Terminated);
            queryDef.WithParameter("@today", DateTime.UtcNow.Date);
        }

        var iterator = _container.GetItemQueryIterator<Coverage>(
            queryDef,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(tenantId)
            });

        var results = new List<Coverage>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    public async Task<(IEnumerable<Coverage> Items, string? ContinuationToken)> SearchAsync(
        string tenantId,
        string? memberId = null,
        string? groupNumber = null,
        string? planId = null,
        bool activeOnly = false,
        int pageSize = 20,
        string? continuationToken = null)
    {
        var queryText = "SELECT * FROM c WHERE c.tenantId = @tenantId";
        var parameters = new List<(string Name, object Value)> { ("@tenantId", tenantId) };

        if (!string.IsNullOrEmpty(memberId))
        {
            queryText += " AND c.memberId = @memberId";
            parameters.Add(("@memberId", memberId));
        }

        if (!string.IsNullOrEmpty(groupNumber))
        {
            queryText += " AND c.groupNumber = @groupNumber";
            parameters.Add(("@groupNumber", groupNumber));
        }

        if (!string.IsNullOrEmpty(planId))
        {
            queryText += " AND c.planId = @planId";
            parameters.Add(("@planId", planId));
        }

        if (activeOnly)
        {
            // Currently active (Coverage.CurrentStatus): Active, or Pending
            // whose effective date has arrived, with the termination date (if
            // any) not yet reached — so the listing doesn't depend on when the
            // daily status sweep last ran.
            queryText += CurrentlyActiveClause;
            parameters.Add(("@activeStatus", (int)CoverageStatus.Active));
            parameters.Add(("@pendingStatus", (int)CoverageStatus.Pending));
            parameters.Add(("@today", DateTime.UtcNow.Date));
        }

        var queryDef = new QueryDefinition(queryText);
        foreach (var (name, value) in parameters)
        {
            queryDef.WithParameter(name, value);
        }

        var iterator = _container.GetItemQueryIterator<Coverage>(
            queryDef,
            continuationToken,
            new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(tenantId),
                MaxItemCount = pageSize
            });

        var results = new List<Coverage>();
        string? newContinuationToken = null;

        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
            newContinuationToken = response.ContinuationToken;
        }

        return (results, newContinuationToken);
    }

    public async Task<List<Coverage>> GetByGroupNumberAsync(string tenantId, string groupNumber)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.groupNumber = @groupNumber")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@groupNumber", groupNumber);

        var iterator = _container.GetItemQueryIterator<Coverage>(query, requestOptions: new QueryRequestOptions
        {
            PartitionKey = new PartitionKey(tenantId)
        });

        var results = new List<Coverage>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    public async Task<List<Coverage>> GetByPcpNpiAsync(
        string tenantId,
        string pcpNpi,
        CoverageStatus? status = null,
        LineOfBusiness? lineOfBusiness = null)
    {
        var queryText = "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.pcpNpi = @pcpNpi";
        var parameters = new List<(string Name, object Value)>
        {
            ("@tenantId", tenantId),
            ("@pcpNpi", pcpNpi)
        };

        if (status == CoverageStatus.Active)
        {
            queryText += CurrentlyActiveClause;
            parameters.Add(("@activeStatus", (int)CoverageStatus.Active));
            parameters.Add(("@pendingStatus", (int)CoverageStatus.Pending));
            parameters.Add(("@today", DateTime.UtcNow.Date));
        }
        else if (status.HasValue)
        {
            queryText += " AND c.status = @status";
            parameters.Add(("@status", (int)status.Value));
        }

        if (lineOfBusiness.HasValue)
        {
            queryText += " AND c.lineOfBusiness = @lineOfBusiness";
            parameters.Add(("@lineOfBusiness", (int)lineOfBusiness.Value));
        }

        var queryDef = new QueryDefinition(queryText);
        foreach (var (name, value) in parameters)
        {
            queryDef.WithParameter(name, value);
        }

        var iterator = _container.GetItemQueryIterator<Coverage>(
            queryDef,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(tenantId)
            });

        var results = new List<Coverage>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    public async Task<int> GetCountByGroupAsync(string tenantId, string groupNumber, CoverageStatus? status = null)
    {
        var queryText = "SELECT VALUE COUNT(1) FROM c WHERE c.tenantId = @tenantId AND c.groupNumber = @groupNumber";
        var queryDef = new QueryDefinition(queryText)
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@groupNumber", groupNumber);

        if (status.HasValue)
        {
            queryText += " AND c.status = @status";
            queryDef = new QueryDefinition(queryText)
                .WithParameter("@tenantId", tenantId)
                .WithParameter("@groupNumber", groupNumber)
                .WithParameter("@status", (int)status.Value);
        }

        var iterator = _container.GetItemQueryIterator<int>(
            queryDef,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(tenantId)
            });

        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            return response.FirstOrDefault();
        }

        return 0;
    }

    public async Task<List<Coverage>> GetStatusTransitionsDueAsync(DateTime today, int maxItems)
    {
        // Cross-partition on purpose: the daily sweep covers every tenant.
        // In-force coverage whose termination date is reached, or Pending
        // whose effective date arrived (Coverage.DueStatusTransition). Only
        // the statuses the sweep will actually change, so rows it skips
        // (Suspended, unknown) never fill a batch and stall it.
        var queryDef = new QueryDefinition(
                "SELECT TOP @maxItems * FROM c WHERE" +
                " (c.status IN (@activeStatus, @pendingStatus, @cobraStatus)" +
                " AND IS_DEFINED(c.terminationDate) AND NOT IS_NULL(c.terminationDate)" +
                " AND c.terminationDate <= @today)" +
                " OR (c.status = @pendingStatus AND c.effectiveDate <= @today)")
            .WithParameter("@maxItems", maxItems)
            .WithParameter("@activeStatus", (int)CoverageStatus.Active)
            .WithParameter("@pendingStatus", (int)CoverageStatus.Pending)
            .WithParameter("@cobraStatus", (int)CoverageStatus.COBRA)
            .WithParameter("@today", today.Date);

        var iterator = _container.GetItemQueryIterator<Coverage>(queryDef);
        var results = new List<Coverage>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }
        return results;
    }

    public async Task<bool> SetStatusAsync(Coverage observed, CoverageStatus newStatus, string updatedBy)
    {
        // Patch only the status/audit fields, and only while the status and
        // the dates the transition was decided from are still the ones the
        // sweep read, so a concurrent edit (PCP change, reinstatement - which
        // clears the termination date and may leave the status unchanged) is
        // neither overwritten nor undone. The current document is compared
        // here and the patch is pinned to its ETag, so a write between the
        // read and the patch fails the precondition too.
        try
        {
            var current = await _container.ReadItemAsync<Coverage>(observed.Id, new PartitionKey(observed.TenantId));
            var stored = current.Resource;
            if (stored is null
                || stored.Status != observed.Status
                || stored.EffectiveDate != observed.EffectiveDate
                || stored.TerminationDate != observed.TerminationDate)
            {
                return false;
            }

            await _container.PatchItemAsync<Coverage>(
                observed.Id,
                new PartitionKey(observed.TenantId),
                new[]
                {
                    PatchOperation.Set("/status", (int)newStatus),
                    PatchOperation.Set("/lastUpdatedDate", DateTime.UtcNow),
                    PatchOperation.Set("/lastUpdatedBy", updatedBy)
                },
                new PatchItemRequestOptions { IfMatchEtag = current.ETag });
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is System.Net.HttpStatusCode.PreconditionFailed
                                             or System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<Coverage> CreateAsync(Coverage coverage)
    {
        coverage.CreatedDate = DateTime.UtcNow;
        coverage.LastUpdatedDate = DateTime.UtcNow;

        var response = await _container.CreateItemAsync(
            coverage,
            new PartitionKey(coverage.TenantId));

        return response.Resource;
    }

    public async Task<Coverage> UpdateAsync(Coverage coverage)
    {
        coverage.LastUpdatedDate = DateTime.UtcNow;

        var response = await _container.ReplaceItemAsync(
            coverage,
            coverage.Id,
            new PartitionKey(coverage.TenantId));

        return response.Resource;
    }

    public async Task DeleteAsync(string tenantId, string id)
    {
        await _container.DeleteItemAsync<Coverage>(
            id,
            new PartitionKey(tenantId));
    }

    // Termination date not yet reached (or none). The query must bind @today.
    private const string NotTerminatedAsOfTodayClause =
        " AND (NOT IS_DEFINED(c.terminationDate) OR IS_NULL(c.terminationDate) OR c.terminationDate > @today)";

    // Coverage.CurrentStatus == Active. Binds @activeStatus, @pendingStatus, @today.
    // An effective COBRA Pending coverage is currently COBRA, not Active; a
    // record without the flag (or with null) is non-COBRA.
    private const string CurrentlyActiveClause =
        " AND (c.status = @activeStatus OR (c.status = @pendingStatus AND c.effectiveDate <= @today" +
        " AND NOT (IS_BOOL(c.isCOBRA) AND c.isCOBRA)))" +
        NotTerminatedAsOfTodayClause;
}

/// <summary>
/// Repository interface for Coverage entities
/// </summary>
public interface ICoverageRepository
{
    Task<Coverage?> GetByIdAsync(string tenantId, string id);
    Task<List<Coverage>> GetActiveCoverageByMemberIdAsync(string tenantId, string memberId, DateTime serviceDate, string? insuranceLineCode = null);
    Task<List<Coverage>> GetCoverageHistoryAsync(string tenantId, string memberId, bool includeTerminated = true);
    Task<(IEnumerable<Coverage> Items, string? ContinuationToken)> SearchAsync(
        string tenantId,
        string? memberId = null,
        string? groupNumber = null,
        string? planId = null,
        bool activeOnly = false,
        int pageSize = 20,
        string? continuationToken = null);
    Task<List<Coverage>> GetByGroupNumberAsync(string tenantId, string groupNumber);
    Task<List<Coverage>> GetByPcpNpiAsync(string tenantId, string pcpNpi, CoverageStatus? status = null, LineOfBusiness? lineOfBusiness = null);
    Task<int> GetCountByGroupAsync(string tenantId, string groupNumber, CoverageStatus? status = null);
    /// <summary>
    /// Coverages in any tenant whose status their date span has moved on as of
    /// <paramref name="today"/> (see <see cref="Coverage.DueStatusTransition"/>),
    /// at most <paramref name="maxItems"/>. Read by the daily status sweep.
    /// </summary>
    Task<List<Coverage>> GetStatusTransitionsDueAsync(DateTime today, int maxItems);
    /// <summary>
    /// Sets the status (and audit fields) of <paramref name="observed"/> only,
    /// and only while the stored status, effective date and termination date
    /// are still the ones in <paramref name="observed"/> (as the sweep read
    /// it). False when any changed concurrently or the coverage is gone.
    /// </summary>
    Task<bool> SetStatusAsync(Coverage observed, CoverageStatus newStatus, string updatedBy);
    Task<Coverage> CreateAsync(Coverage coverage);
    Task<Coverage> UpdateAsync(Coverage coverage);
    Task DeleteAsync(string tenantId, string id);
}
