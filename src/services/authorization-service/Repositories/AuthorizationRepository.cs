using System.Text.Json;
using Microsoft.Azure.Cosmos;
using AuthorizationService.Models;
using AuthorizationService.Services.Retention;

namespace AuthorizationService.Repositories;

public interface IAuthorizationRepository
{
    Task<Authorization?> GetByIdAsync(string id);
    Task<Authorization?> GetByAuthorizationNumberAsync(string authorizationNumber);
    Task<IEnumerable<Authorization>> SearchAsync(
        string? memberId,
        string? providerNPI,
        DateTime? serviceDateFrom,
        DateTime? serviceDateTo,
        AuthorizationStatus? status,
        LineOfBusiness? lineOfBusiness,
        int page,
        int pageSize);
    Task<AuthorizationsSummary> GetAuthorizationsSummaryAsync(DateTime from, DateTime to, LineOfBusiness? lineOfBusiness);
    Task<IEnumerable<Authorization>> GetOpenAuthorizationsAsync(string? tenantId = null);
    /// <summary>
    /// The current tenant's authorizations submitted or decided in [from, to],
    /// for CMS-0057-F reporting (metrics and the recent-decision list).
    /// </summary>
    Task<IEnumerable<Authorization>> ListActivityBetweenAsync(DateTime from, DateTime to);

    /// <summary>
    /// The current tenant's approved authorizations (Approved or Modified)
    /// whose approved service period ended in [from, to]: the approved end
    /// date, or the expiration date when no end date was approved.
    /// </summary>
    Task<IReadOnlyList<Authorization>> ListApprovedEndingBetweenAsync(DateTime from, DateTime to)
        => throw new NotSupportedException("Approved authorizations by end date need the MongoDB authorization store.");
    Task<Authorization> CreateAsync(Authorization authorization);
    Task<Authorization> UpdateAsync(Authorization authorization);
    Task DeleteAsync(string id);

    // ── Retention (PAT-03) ───────────────────────────────────────────────────
    // Background-callable: every method takes the tenant EXPLICITLY, so a sweep
    // never depends on an ambient HttpContext, and a CancellationToken so a
    // shutdown stops mid-sweep rather than at the end of it.

    /// <summary>
    /// Tenants that have authorization data, so a sweep can iterate them
    /// without a second service. Mirrors provider-service's
    /// <c>ListProviderTenantIdsAsync</c>.
    /// </summary>
    Task<IReadOnlyList<string>> ListTenantIdsAsync(CancellationToken ct = default);

    /// <summary>
    /// Terminal authorizations for one tenant whose retention anchor is at or
    /// before <paramref name="anchorCutoffUtc"/>, capped at
    /// <paramref name="limit"/>. Coarse filtering only — the caller applies the
    /// retention policy per record before deleting anything.
    /// </summary>
    Task<IReadOnlyList<Authorization>> FindRetentionCandidatesAsync(
        string tenantId, DateTime anchorCutoffUtc, int limit, CancellationToken ct = default);

    /// <summary>
    /// Deletes one authorization ONLY IF it is still in
    /// <paramref name="expectedStatus"/>. Returns false when the record moved on
    /// or was already gone, so a sweep cannot delete a record that became active
    /// again between being listed and being purged. Tenant is explicit and
    /// always part of the delete predicate.
    /// </summary>
    Task<bool> PurgeIfStillEligibleAsync(
        string tenantId, string id, AuthorizationStatus expectedStatus, CancellationToken ct = default);

    // ── SLA watchdog (D-07) ──────────────────────────────────────────────────
    // Background-callable like the retention methods: the tenant is explicit,
    // and GetOpenAuthorizationsAsync(tenantId) lists that tenant's open records.

    /// <summary>
    /// Raises one authorization's SLA escalation to <paramref name="level"/>
    /// and stamps <paramref name="escalatedAtUtc"/>, ONLY IF it belongs to
    /// <paramref name="tenantId"/>, is still open and is below that level.
    /// Writes nothing else, so a reviewer's concurrent change is never
    /// overwritten. Returns false when the record moved on, was already at or
    /// above the level, or is gone.
    /// </summary>
    Task<bool> RaiseSlaEscalationAsync(
        string tenantId, string id, SlaEscalationLevel level, DateTime escalatedAtUtc, CancellationToken ct = default);
}

/// <summary>
/// Cosmos DB repository. Documents are written by the host's shared
/// <c>CosmosSystemTextJsonSerializer</c> (registered by <c>AddChoDatabase</c>):
/// camelCase names, enums as camelCase strings, nulls omitted. Queries
/// therefore bind enum values as PARAMETERS, so the same serializer writes
/// them exactly as it stores the property (D-18), and treat a missing
/// property as null (<c>NOT IS_DEFINED(x) OR IS_NULL(x)</c>), never
/// <c>x = null</c>.
/// </summary>
public class AuthorizationRepository : IAuthorizationRepository
{
    // The open/terminal split is defined once (AuthorizationStatusExtensions).
    private static readonly AuthorizationStatus[] OpenStatuses =
        Enum.GetValues<AuthorizationStatus>().Where(s => s.IsOpen()).ToArray();

    private static readonly AuthorizationStatus[] TerminalStatuses =
        Enum.GetValues<AuthorizationStatus>().Where(s => s.IsTerminal()).ToArray();

    private readonly Container _container;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AuthorizationRepository> _logger;

    public AuthorizationRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<AuthorizationRepository> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        var containerName = configuration["CosmosDb:ContainerName"] ?? "Authorizations";

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

    public async Task<Authorization?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();

        try
        {
            var response = await _container.ReadItemAsync<Authorization>(
                id,
                new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Authorization?> GetByAuthorizationNumberAsync(string authorizationNumber)
    {
        var tenantId = GetTenantId();

        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.authorizationNumber = @authorizationNumber")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@authorizationNumber", authorizationNumber);

        var iterator = _container.GetItemQueryIterator<Authorization>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        var results = new List<Authorization>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results.FirstOrDefault();
    }

    public async Task<IEnumerable<Authorization>> SearchAsync(
        string? memberId,
        string? providerNPI,
        DateTime? serviceDateFrom,
        DateTime? serviceDateTo,
        AuthorizationStatus? status,
        LineOfBusiness? lineOfBusiness,
        int page,
        int pageSize)
    {
        var tenantId = GetTenantId();

        // Build dynamic query
        var conditions = new List<string> { "c.tenantId = @tenantId" };
        var parameters = new Dictionary<string, object> { { "@tenantId", tenantId } };

        if (!string.IsNullOrEmpty(memberId))
        {
            conditions.Add("c.memberId = @memberId");
            parameters["@memberId"] = memberId;
        }

        if (!string.IsNullOrEmpty(providerNPI))
        {
            conditions.Add("(c.requestingProviderNPI = @providerNPI OR c.servicingProviderNPI = @providerNPI)");
            parameters["@providerNPI"] = providerNPI;
        }

        if (serviceDateFrom.HasValue)
        {
            conditions.Add("c.requestedServiceDateFrom >= @serviceDateFrom");
            parameters["@serviceDateFrom"] = serviceDateFrom.Value;
        }

        if (serviceDateTo.HasValue)
        {
            // An open-ended request has no stored requestedServiceDateTo (nulls
            // are omitted), and "x = null" is undefined for a missing property.
            conditions.Add("(NOT IS_DEFINED(c.requestedServiceDateTo) OR IS_NULL(c.requestedServiceDateTo) "
                           + "OR c.requestedServiceDateTo <= @serviceDateTo)");
            parameters["@serviceDateTo"] = serviceDateTo.Value;
        }

        if (status.HasValue)
        {
            conditions.Add("c.status = @status");
            parameters["@status"] = status.Value;
        }

        if (lineOfBusiness.HasValue)
        {
            conditions.Add("c.lineOfBusiness = @lineOfBusiness");
            parameters["@lineOfBusiness"] = lineOfBusiness.Value;
        }

        var queryText = $@"
            SELECT * FROM c 
            WHERE {string.Join(" AND ", conditions)} 
            ORDER BY c.submittedDate DESC 
            OFFSET {(page - 1) * pageSize} LIMIT {pageSize}";

        var queryDef = new QueryDefinition(queryText);
        foreach (var (key, value) in parameters)
        {
            queryDef.WithParameter(key, value);
        }

        var iterator = _container.GetItemQueryIterator<Authorization>(
            queryDef, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        var results = new List<Authorization>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    public async Task<AuthorizationsSummary> GetAuthorizationsSummaryAsync(
        DateTime from,
        DateTime to,
        LineOfBusiness? lineOfBusiness)
    {
        var tenantId = GetTenantId();

        var lobCondition = lineOfBusiness.HasValue
            ? "AND c.lineOfBusiness = @lineOfBusiness"
            : "";
        var tenantPartition = new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) };

        QueryDefinition Query(string text)
        {
            var query = new QueryDefinition(text)
                .WithParameter("@tenantId", tenantId)
                .WithParameter("@from", from)
                .WithParameter("@to", to);
            // Bound as the enum itself, so the serializer writes it in its stored form.
            if (lineOfBusiness.HasValue)
                query = query.WithParameter("@lineOfBusiness", lineOfBusiness.Value);
            return query;
        }

        // Counts per status. GROUP BY reads the status back through the same
        // serializer that stored it, so no status literal appears in the query.
        var counts = new Dictionary<AuthorizationStatus, int>();
        var countIterator = _container.GetItemQueryIterator<StatusCountRow>(
            Query($@"
                SELECT c.status AS status, COUNT(1) AS n
                FROM c
                WHERE c.tenantId = @tenantId
                AND c.submittedDate >= @from
                AND c.submittedDate <= @to
                {lobCondition}
                GROUP BY c.status"),
            requestOptions: tenantPartition);
        while (countIterator.HasMoreResults)
        {
            foreach (var row in await countIterator.ReadNextAsync())
            {
                if (row.Status is { } rowStatus)
                    counts[rowStatus] = counts.GetValueOrDefault(rowStatus) + row.N;
            }
        }

        int Count(AuthorizationStatus status) => counts.GetValueOrDefault(status);

        var summary = new AuthorizationsSummary
        {
            TotalAuthorizations = counts.Values.Sum(),
            ApprovedAuthorizations = Count(AuthorizationStatus.Approved),
            DeniedAuthorizations = Count(AuthorizationStatus.Denied),
            PendedAuthorizations = Count(AuthorizationStatus.Pended),
            ModifiedAuthorizations = Count(AuthorizationStatus.Modified),
            ExpiredAuthorizations = Count(AuthorizationStatus.Expired),
        };

        // Calculate approval rate (approved + modified)
        if (summary.TotalAuthorizations > 0)
        {
            summary.ApprovalRate = (decimal)(summary.ApprovedAuthorizations + summary.ModifiedAuthorizations) /
                                   summary.TotalAuthorizations * 100;
        }

        const string reviewed = "IS_DEFINED(c.reviewedDate) AND NOT IS_NULL(c.reviewedDate)";

        // AverageReviewDays: raw submission-to-decision time (always from SubmittedDate)
        summary.AverageReviewDays = await AverageAsync(Query($@"
            SELECT VALUE AVG(DateTimeDiff('day', c.submittedDate, c.reviewedDate))
            FROM c
            WHERE c.tenantId = @tenantId
            AND c.submittedDate >= @from
            AND c.submittedDate <= @to
            AND {reviewed}
            {lobCondition}"), tenantPartition);

        // AverageTurnaroundDays: SLA-adjusted time (from SlaResumedAt when RFAI
        // was issued). A record never resumed has no stored slaResumedAt.
        summary.AverageTurnaroundDays = await AverageAsync(Query($@"
            SELECT VALUE AVG(
                DateTimeDiff('day',
                    IIF(IS_DEFINED(c.slaResumedAt) AND NOT IS_NULL(c.slaResumedAt), c.slaResumedAt, c.submittedDate),
                    c.reviewedDate))
            FROM c
            WHERE c.tenantId = @tenantId
            AND c.submittedDate >= @from
            AND c.submittedDate <= @to
            AND {reviewed}
            {lobCondition}"), tenantPartition);

        return summary;
    }

    private async Task<decimal> AverageAsync(QueryDefinition query, QueryRequestOptions options)
    {
        var iterator = _container.GetItemQueryIterator<double?>(query, requestOptions: options);
        while (iterator.HasMoreResults)
        {
            foreach (var value in await iterator.ReadNextAsync())
            {
                if (value is { } average)
                    return (decimal)average;
            }
        }

        // No reviewed authorizations in range: AVG has no value.
        return 0;
    }

    /// <summary>One row of the per-status count; read with the container's serializer.</summary>
    private sealed class StatusCountRow
    {
        public AuthorizationStatus? Status { get; set; }

        public int N { get; set; }
    }

    // ── Retention (PAT-03) ───────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> ListTenantIdsAsync(CancellationToken ct = default)
    {
        var queryDef = new QueryDefinition("SELECT DISTINCT VALUE c.tenantId FROM c");
        var iterator = _container.GetItemQueryIterator<string>(queryDef);
        var tenants = new List<string>();

        while (iterator.HasMoreResults)
        {
            ct.ThrowIfCancellationRequested();
            var response = await iterator.ReadNextAsync(ct);
            tenants.AddRange(response.Where(t => !string.IsNullOrWhiteSpace(t)));
        }

        return tenants;
    }

    public async Task<IReadOnlyList<Authorization>> FindRetentionCandidatesAsync(
        string tenantId, DateTime anchorCutoffUtc, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant is required for a retention sweep.", nameof(tenantId));

        // Terminal statuses only, and bounded. The date predicate uses
        // submittedDate as a COARSE floor: a record submitted after the cutoff
        // cannot possibly have a last status change before it, so this is a safe
        // over-select. The policy then decides per record from the real anchor.
        //
        // ORDERED oldest-first: a bounded query without a sort returns whatever
        // subset the store happens to hand back, so a large backlog could be
        // re-scanned indefinitely while the oldest records are never reached.
        // Oldest-first makes each sweep advance.
        var queryText = $@"
            SELECT * FROM c
            WHERE c.tenantId = @tenantId
            AND c.status IN ({StatusParameterList("@terminal", TerminalStatuses.Length)})
            AND c.submittedDate <= @cutoff
            ORDER BY c.submittedDate ASC
            OFFSET 0 LIMIT @limit";

        var queryDef = WithStatusParameters(new QueryDefinition(queryText), "@terminal", TerminalStatuses)
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@cutoff", anchorCutoffUtc)
            .WithParameter("@limit", limit);

        var iterator = _container.GetItemQueryIterator<Authorization>(
            queryDef,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(tenantId),
                MaxItemCount = limit,
            });

        var results = new List<Authorization>();
        while (iterator.HasMoreResults && results.Count < limit)
        {
            ct.ThrowIfCancellationRequested();
            var response = await iterator.ReadNextAsync(ct);
            results.AddRange(response);
        }

        return results.Count > limit ? results.Take(limit).ToList() : results;
    }

    public async Task<bool> PurgeIfStillEligibleAsync(
        string tenantId, string id, AuthorizationStatus expectedStatus, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant is required for a retention purge.", nameof(tenantId));

        var partition = new PartitionKey(tenantId);

        try
        {
            // Re-read inside the purge and carry the ETag into the delete, so a
            // record that changed between being listed and being deleted fails
            // the precondition instead of being removed. Authorization has no
            // version field of its own; the store's ETag is the concurrency
            // token available here.
            var current = await _container.ReadItemAsync<Authorization>(id, partition, cancellationToken: ct);

            if (current.Resource is null
                || current.Resource.Status != expectedStatus
                || current.Resource.Status.IsOpen())
            {
                return false;
            }

            await _container.DeleteItemAsync<Authorization>(
                id,
                partition,
                new ItemRequestOptions { IfMatchEtag = current.ETag },
                ct);

            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already gone. A repeated sweep is a no-op, not a failure.
            return false;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            // Changed under us. Leave it; the next sweep re-evaluates.
            return false;
        }
    }

    public async Task<IEnumerable<Authorization>> GetOpenAuthorizationsAsync(string? tenantId = null)
    {
        var effectiveTenantId = tenantId ?? GetTenantId();

        var queryText = $@"
            SELECT * FROM c
            WHERE c.tenantId = @tenantId
            AND c.status IN ({StatusParameterList("@open", OpenStatuses.Length)})";

        var queryDef = WithStatusParameters(new QueryDefinition(queryText), "@open", OpenStatuses)
            .WithParameter("@tenantId", effectiveTenantId);

        // Scoped to the tenant's partition as well as filtered on it, so a
        // background caller can never read another tenant's records.
        var iterator = _container.GetItemQueryIterator<Authorization>(
            queryDef,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(effectiveTenantId) });
        var results = new List<Authorization>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    public async Task<IEnumerable<Authorization>> ListActivityBetweenAsync(DateTime from, DateTime to)
    {
        var tenantId = GetTenantId();
        var queryDef = new QueryDefinition(@"
            SELECT * FROM c
            WHERE c.tenantId = @tenantId
            AND ((c.submittedDate >= @from AND c.submittedDate <= @to)
              OR (IS_DEFINED(c.reviewedDate) AND NOT IS_NULL(c.reviewedDate)
                  AND c.reviewedDate >= @from AND c.reviewedDate <= @to))")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@from", from)
            .WithParameter("@to", to);

        var iterator = _container.GetItemQueryIterator<Authorization>(
            queryDef,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        var results = new List<Authorization>();
        while (iterator.HasMoreResults)
        {
            results.AddRange(await iterator.ReadNextAsync());
        }
        return results;
    }

    public async Task<bool> RaiseSlaEscalationAsync(
        string tenantId, string id, SlaEscalationLevel level, DateTime escalatedAtUtc, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant is required for an SLA escalation.", nameof(tenantId));

        var partition = new PartitionKey(tenantId);

        try
        {
            // Read in the tenant's partition to check the conditions, then PATCH
            // only the two escalation fields under the read's ETag. A patch never
            // rewrites the rest of the document, so properties this version does
            // not model (a newer pod's, during a rolling deploy) survive, and a
            // record a reviewer changed in between fails the precondition (412).
            var current = await _container.ReadItemAsync<Authorization>(id, partition, cancellationToken: ct);
            var auth = current.Resource;

            if (auth is null
                || !string.Equals(auth.TenantId, tenantId, StringComparison.Ordinal)
                || !auth.Status.IsOpen()
                || auth.SlaEscalation >= level)
            {
                return false;
            }

            var (storedLevel, storedAt) = StoredEscalationValues(level, escalatedAtUtc);

            await _container.PatchItemAsync<Authorization>(
                id,
                partition,
                new[]
                {
                    PatchOperation.Set("/slaEscalation", storedLevel),
                    PatchOperation.Set("/slaEscalatedAt", storedAt),
                },
                new PatchItemRequestOptions { IfMatchEtag = current.ETag },
                ct);

            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound
                                              or System.Net.HttpStatusCode.PreconditionFailed)
        {
            // Gone, or changed under us. The next tick re-evaluates.
            return false;
        }
    }

    /// <summary>
    /// The escalation fields exactly as a full write stores them: a probe
    /// <see cref="Authorization"/> goes through the client's own serializer, so
    /// the property-level enum converter on <c>SlaEscalation</c> ("Warning")
    /// and the date format apply. The patch carries those JSON values as-is.
    /// </summary>
    private (JsonElement Level, JsonElement At) StoredEscalationValues(SlaEscalationLevel level, DateTime at)
    {
        var serializer = _container.Database.Client.ClientOptions.Serializer
                         ?? throw new InvalidOperationException(
                             "The Cosmos client has no custom serializer; AddChoDatabase registers the shared one.");
        var probe = new Authorization { SlaEscalation = level, SlaEscalatedAt = at };
        using var stream = serializer.ToStream(probe);
        using var document = JsonDocument.Parse(stream);
        return (document.RootElement.GetProperty("slaEscalation").Clone(),
                document.RootElement.GetProperty("slaEscalatedAt").Clone());
    }

    private static string StatusParameterList(string prefix, int count) =>
        string.Join(", ", Enumerable.Range(0, count).Select(i => $"{prefix}{i}"));

    /// <summary>
    /// Binds each status as the enum itself, so the client's serializer writes
    /// it in exactly the form it stores <c>status</c> (D-18).
    /// </summary>
    private static QueryDefinition WithStatusParameters(
        QueryDefinition query, string prefix, IReadOnlyList<AuthorizationStatus> statuses)
    {
        for (var i = 0; i < statuses.Count; i++)
            query = query.WithParameter($"{prefix}{i}", statuses[i]);
        return query;
    }

    public async Task<Authorization> CreateAsync(Authorization authorization)
    {
        var tenantId = GetTenantId();
        authorization.TenantId = tenantId;

        var response = await _container.CreateItemAsync(authorization, new PartitionKey(tenantId));
        return response.Resource;
    }

    public async Task<Authorization> UpdateAsync(Authorization authorization)
    {
        var tenantId = GetTenantId();
        authorization.TenantId = tenantId;

        var response = await _container.ReplaceItemAsync(
            authorization,
            authorization.Id,
            new PartitionKey(tenantId));
        return response.Resource;
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();
        await _container.DeleteItemAsync<Authorization>(id, new PartitionKey(tenantId));
    }
}
