using Microsoft.Azure.Cosmos;
using ProviderService.Models;

namespace ProviderService.Repositories;

/// <summary>
/// Storage seam for the provider version chain. Each row in the underlying
/// collection is one immutable version. Default reads (the non-version-aware
/// overloads kept for backward compatibility) resolve to the latest
/// <see cref="ProviderVersionState.Active"/> version effective today.
/// </summary>
public interface IProviderRepository
{
    Task<Provider?> GetByIdAsync(string id);
    Task<Provider?> GetByNPIAsync(string npi);
    /// <summary>
    /// General provider search across the tenant's head non-Draft rows.
    /// Filters are AND-combined; null / empty values are skipped.
    ///
    /// <para>
    /// <paramref name="firstName"/>, <paramref name="lastName"/>, and
    /// <paramref name="city"/> were added in capability 5.7 to support
    /// FHIR Practitioner search semantics (<c>given</c> / <c>family</c> /
    /// <c>city</c>). They are optional; legacy callers (the adapter
    /// roster path) leave them null and continue to use the combined
    /// <paramref name="name"/> filter.
    /// </para>
    /// </summary>
    Task<IEnumerable<Provider>> SearchAsync(
        string? name,
        string? specialty,
        string? zipCode,
        string? state,
        string? planId,
        LineOfBusiness? lineOfBusiness,
        ProviderType? providerType,
        bool? acceptingNewPatients,
        int page,
        int pageSize,
        string? firstName = null,
        string? lastName = null,
        string? city = null);
    Task<Provider> CreateAsync(Provider provider);
    Task<Provider> UpdateAsync(Provider provider);
    Task DeleteAsync(string id);

    /// <summary>
    /// Backing query for <c>GET /api/v1/networks/{id}/roster</c>. Matches
    /// the latest non-Draft head row for each provider in the tenant that
    /// has a <see cref="NetworkParticipation"/> with
    /// <c>NetworkId == query.NetworkId</c> AND every other supplied filter
    /// AND (when <c>AsOfDate</c> is set) a participation period covering
    /// that date. Sort + paging are applied at the repository layer so
    /// the service never has to re-page.
    ///
    /// <para>
    /// <paramref name="skip"/> is the offset already decoded from the
    /// caller's cursor. The repository returns at most <c>pageSize</c>
    /// rows.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<Provider>> ListNetworkRosterAsync(
        NetworkRosterQuery query,
        NetworkRosterSort sort,
        int skip,
        CancellationToken ct = default);

    // ---- Version-chain operations ------------------------------------

    /// <summary>
    /// Latest <see cref="ProviderVersionState.Active"/> version of
    /// <paramref name="providerId"/> in effect at <paramref name="asOf"/>.
    /// Returns null when no Active version exists (terminated, suspended,
    /// or never activated).
    /// </summary>
    Task<Provider?> GetLatestActiveAsync(string providerId, DateTime asOf);

    /// <summary>Look up a single version by <c>VersionId</c>.</summary>
    Task<Provider?> GetVersionAsync(string providerId, string versionId);

    /// <summary>
    /// Newest-first list of every version for <paramref name="providerId"/>,
    /// paginated with a continuation token.
    /// </summary>
    Task<(IReadOnlyList<Provider> Items, string? ContinuationToken)> ListVersionsAsync(
        string providerId, int pageSize, string? continuationToken);

    /// <summary>
    /// Persist a new draft. Caller is responsible for setting
    /// <c>VersionId</c>, <c>VersionNumber</c>, <c>VersionState=Draft</c>
    /// and (for amendments) <c>PredecessorVersionId</c>.
    /// </summary>
    Task<Provider> CreateDraftAsync(Provider draft);

    /// <summary>Update a Draft. Throws <see cref="ProviderVersionStateException"/> if the row is not Draft.</summary>
    Task<Provider> UpdateDraftAsync(Provider draft);

    /// <summary>
    /// Atomic transition: flip <paramref name="draftToActivate"/> from Draft
    /// to Active and (if not null) flip <paramref name="predecessor"/> from
    /// Active/Suspended/Terminated to Superseded with
    /// <c>SupersededByVersionId = draftToActivate.VersionId</c>. Implementations
    /// use a transactional batch (Cosmos) or session transaction (Mongo)
    /// when the backend supports it; otherwise they fall back to sequential
    /// writes and log a compensating-action warning.
    /// </summary>
    Task<Provider> ActivateAndSupersedeAsync(Provider draftToActivate, Provider? predecessor);

    /// <summary>
    /// Persist a state-only mutation on an existing version row (Suspend
    /// or Terminate). The service layer applies the new state and
    /// timestamps before calling. Bypasses the Active-is-read-only guard
    /// in <see cref="UpdateAsync"/>.
    /// </summary>
    Task<Provider> ReplaceVersionRowAsync(Provider version);

    // ---- Integrity projection write-back (capability 5.4.5) ----------

    /// <summary>
    /// Patch the four cached integrity-projection fields
    /// (<see cref="Provider.IntegrityScore"/>,
    /// <see cref="Provider.IntegrityRating"/>,
    /// <see cref="Provider.LastVerifiedAt"/>,
    /// <see cref="Provider.NextVerificationDue"/>) on the head Active
    /// version of <paramref name="providerId"/>. No new version row is
    /// created; identity-version semantics from PR 7.2 are preserved.
    ///
    /// <para>
    /// These fields are *projection metadata*, not provider-identity
    /// fields — see <c>docs/architecture/provider-versioning.md</c>
    /// "Projection metadata — exempt from versioning". The bypass is
    /// implemented as a separate write path: the Cosmos impl uses
    /// <c>PatchItemAsync</c> with field-scoped <c>Set</c> ops and the
    /// Mongo impl uses <c>UpdateOneAsync</c> with <c>$set</c> on the
    /// four field paths only. Neither path goes through
    /// <see cref="UpdateAsync"/>'s version-state guard.
    /// </para>
    ///
    /// <para>
    /// Returns <c>true</c> when the head Active version was patched;
    /// <c>false</c> when no Active head exists (Suspended / Superseded /
    /// Terminated / never-activated). Idempotent: rerunning with the same
    /// inputs is a no-op overwrite. Does not throw on missing chains.
    /// </para>
    /// </summary>
    Task<bool> UpdateIntegrityProjectionAsync(
        string tenantId,
        string providerId,
        int? integrityScore,
        string? integrityRating,
        DateTimeOffset? lastVerifiedAt,
        DateTimeOffset? nextVerificationDue,
        CancellationToken ct = default);

    /// <summary>
    /// Page of head-Active providers in <paramref name="tenantId"/>
    /// whose <see cref="Provider.NextVerificationDue"/> has elapsed
    /// (<c>&lt;= dueBefore</c>) or is null when
    /// <paramref name="includeNeverVerified"/> is true. Stable sort
    /// (<c>ProviderId</c> asc) so <paramref name="skip"/> pagination is
    /// deterministic across sweeps.
    ///
    /// <para>
    /// Used by <c>IntegrityProjectionWorker</c>. Tenant id is taken
    /// explicitly because the worker runs without an HTTP context — the
    /// usual <see cref="IHttpContextAccessor"/> tenant resolution doesn't
    /// apply.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<Provider>> ListProvidersForIntegrityRefreshAsync(
        string tenantId,
        DateTimeOffset dueBefore,
        bool includeNeverVerified,
        int skip,
        int pageSize,
        CancellationToken ct = default);

    /// <summary>
    /// Distinct <see cref="Provider.TenantId"/>s across the Providers
    /// collection. The worker uses this to iterate per-tenant on each
    /// sweep without depending on a separate tenant catalogue.
    ///
    /// <para>
    /// Cross-partition scan; the worker calls this once per sweep
    /// interval (default 1h) so RU/IO cost is bounded.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<string>> ListProviderTenantIdsAsync(CancellationToken ct = default);

    /// <summary>
    /// Count head-Active providers in <paramref name="tenantId"/> whose
    /// <see cref="Provider.LastVerifiedAt"/> is <c>null</c> or older than
    /// <paramref name="staleBefore"/>. Used by
    /// <c>IntegrityProjectionStalenessReporter</c> (capability 5.10) to
    /// publish the per-tenant
    /// <c>cho.provider.integrity_score.stale_count</c> Prometheus gauge.
    ///
    /// <para>
    /// Mirrors the hydration rule of
    /// <see cref="ListProvidersForIntegrityRefreshAsync"/> — only
    /// head-Active rows are considered (legacy single-row chains, missing
    /// <c>VersionState</c> field, and explicit Active state). Per-tenant
    /// scoped (single-partition on the Cosmos impl) so the gauge update
    /// stays cheap on the worker hot-path.
    /// </para>
    /// </summary>
    Task<long> CountStaleProvidersAsync(
        string tenantId,
        DateTimeOffset staleBefore,
        CancellationToken ct = default);

    // ---- Network-participation panel-gating backfill (capability 5.5) -

    /// <summary>
    /// Patch the five panel-gating fields
    /// (<see cref="NetworkParticipation.PanelLimit"/>,
    /// <see cref="NetworkParticipation.PanelAccepted"/>,
    /// <see cref="NetworkParticipation.AcceptedLobs"/>,
    /// <see cref="NetworkParticipation.MinAcceptedAgeYears"/>,
    /// <see cref="NetworkParticipation.MaxAcceptedAgeYears"/>) on a
    /// single <see cref="NetworkParticipation"/> within the head Active
    /// version of <paramref name="providerId"/>, addressed by
    /// <paramref name="participationIndex"/>. No new version row is
    /// created; identity-version semantics from PR 7.2 are preserved.
    ///
    /// <para>
    /// Panel-gating defaults applied to legacy rows during backfill are
    /// operational maintenance, not identity changes — see
    /// <c>docs/architecture/provider-versioning.md</c> "Operational
    /// backfill — one-time exemption". The bypass is implemented as a
    /// separate write path: the Cosmos impl uses
    /// <c>PatchItemAsync</c> with field-scoped <c>Set</c> ops on the
    /// positional participation slot, and the Mongo impl uses
    /// <c>FindOneAndUpdateAsync</c> with <c>$set</c> on the same slot
    /// (FindOneAndUpdate is required because the write sorts by
    /// <c>VersionNumber</c> descending to hit the head when historical
    /// Superseded rows exist). Neither path goes through
    /// <see cref="UpdateAsync"/>'s version-state guard. The exemption
    /// applies ONLY to this method; going-forward writes through
    /// <see cref="UpdateAsync"/> still require Draft state.
    /// </para>
    ///
    /// <para>
    /// Returns <c>true</c> when the head Active version was patched;
    /// <c>false</c> when no Active head exists, the index is out of
    /// range on the read-side document, or an etag-conflict caused the
    /// patch to skip. Value-preserving on rerun: writing the same
    /// type-default inputs against an already-patched row produces no
    /// observable data change, but DOES return <c>true</c> (a fresh
    /// patch was applied). Does not throw on missing chains.
    /// </para>
    /// </summary>
    Task<bool> UpdatePanelGatingDefaultsAsync(
        string tenantId,
        string providerId,
        int participationIndex,
        PanelGatingFields fields,
        CancellationToken ct = default);

    /// <summary>
    /// Page of head-Active providers in <paramref name="tenantId"/>
    /// whose <see cref="Provider.NetworkParticipations"/> contains at
    /// least one participation in the legacy-unconstrained shape (all
    /// five panel-gating fields at type defaults). Stable sort
    /// (<c>ProviderId</c> asc, <c>Id</c> asc) so <paramref name="skip"/>
    /// pagination is deterministic across iterations.
    ///
    /// <para>
    /// Used by <c>NetworkParticipationBackfillService</c>. Tenant id is
    /// taken explicitly because the admin endpoint resolves the tenant
    /// from a query parameter, not the usual
    /// <see cref="IHttpContextAccessor"/> middleware.
    /// </para>
    ///
    /// <para>
    /// The eligibility filter is best-effort at the storage layer:
    /// implementations may return a superset (rows with at least one
    /// participation field unset). The service layer applies the
    /// authoritative all-five-defaults check before patching, so a
    /// false-positive page entry is just a no-op skip — never a
    /// data-corruption risk.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<Provider>> ListProvidersForPanelGatingBackfillAsync(
        string tenantId,
        int skip,
        int pageSize,
        CancellationToken ct = default);

    // ---- Credentialing projection write-back (capability 5.6) --------

    /// <summary>
    /// Patch the three credentialing-projection fields
    /// (<see cref="Provider.CredentialingStatus"/>,
    /// <see cref="Provider.CredentialingDate"/>,
    /// <see cref="Provider.RecredentialingDueDate"/>) on the head Active
    /// version of <paramref name="providerId"/>. Mirrors
    /// <see cref="UpdateIntegrityProjectionAsync"/> — same hydration
    /// rule, same exemption from the version-state guard.
    ///
    /// <para>
    /// These fields are *projection metadata*, not provider-identity
    /// fields — see <c>docs/architecture/provider-versioning.md</c>
    /// "Projection metadata — exempt from versioning". The bypass is
    /// implemented as a separate write path: the Cosmos impl uses
    /// <c>PatchItemAsync</c> with field-scoped <c>Set</c> ops and the
    /// Mongo impl uses <c>FindOneAndUpdateAsync</c> with <c>$set</c> on
    /// the three field paths only. Neither path goes through
    /// <see cref="UpdateAsync"/>'s version-state guard.
    /// </para>
    ///
    /// <para>
    /// Returns <c>true</c> when the head Active version was patched;
    /// <c>false</c> when no Active head exists. Idempotent — rerunning
    /// with the same inputs is a no-op overwrite. Does not throw on
    /// missing chains.
    /// </para>
    /// </summary>
    Task<bool> UpdateCredentialingProjectionAsync(
        string tenantId,
        string providerId,
        CredentialingStatus status,
        DateTime? credentialingDate,
        DateTime? recredentialingDueDate,
        CancellationToken ct = default);
}

/// <summary>
/// Thrown when a write violates the version-state invariants — e.g. an
/// attempt to update an Active row, to activate a non-Draft row, or to
/// reach a version that doesn't exist. The controller boundary maps
/// <see cref="IsNotFound"/> to HTTP 404 and everything else to 409.
/// </summary>
public sealed class ProviderVersionStateException : InvalidOperationException
{
    public string ProviderId { get; }
    public string VersionId { get; }
    public ProviderVersionState CurrentState { get; }

    /// <summary>
    /// True when the underlying cause is "the requested provider/version
    /// does not exist", as opposed to a state-machine violation. Set on
    /// construction; controllers map this to HTTP 404 instead of 409.
    /// </summary>
    public bool IsNotFound { get; init; }

    public ProviderVersionStateException(string providerId, string versionId, ProviderVersionState currentState, string message)
        : base(message)
    {
        ProviderId = providerId;
        VersionId = versionId;
        CurrentState = currentState;
    }
}

public class ProviderRepository : IProviderRepository
{
    private readonly Container _container;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ProviderRepository> _logger;

    public ProviderRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ProviderRepository> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "ProviderDB";
        var containerName = configuration["CosmosDb:ContainerName"] ?? "Providers";

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

    public async Task<Provider?> GetByIdAsync(string id)
    {
        // Resolves the chain key (ProviderId) to the latest non-Draft row.
        // For legacy single-row chains where ProviderId is empty on disk,
        // hydration restores ProviderId = Id, so the same call returns the
        // same row it always did.
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.tenantId = @tenantId AND " +
                "(c.providerId = @id OR (NOT IS_DEFINED(c.providerId) AND c.id = @id)) AND " +
                "(NOT IS_DEFINED(c.versionState) OR c.versionState != @draft) " +
                "ORDER BY c.versionNumber DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@id", id)
            .WithParameter("@draft", ProviderVersionState.Draft.ToString());

        var iterator = _container.GetItemQueryIterator<Provider>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            var first = page.FirstOrDefault();
            if (first != null) return Hydrate(first);
        }
        return null;
    }

    public async Task<Provider?> GetByNPIAsync(string npi)
    {
        var tenantId = GetTenantId();

        // Skip Draft rows so NPI lookups consistently resolve to the head
        // non-Draft version (Active / Suspended / Terminated / Superseded).
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.npi = @npi AND " +
            "(NOT IS_DEFINED(c.versionState) OR c.versionState != @draft) " +
            "ORDER BY c.versionNumber DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@npi", npi)
            .WithParameter("@draft", ProviderVersionState.Draft.ToString());

        var iterator = _container.GetItemQueryIterator<Provider>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        var results = new List<Provider>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results.Select(Hydrate).FirstOrDefault();
    }

    public async Task<IEnumerable<Provider>> SearchAsync(
        string? name,
        string? specialty,
        string? zipCode,
        string? state,
        string? planId,
        LineOfBusiness? lineOfBusiness,
        ProviderType? providerType,
        bool? acceptingNewPatients,
        int page,
        int pageSize,
        string? firstName = null,
        string? lastName = null,
        string? city = null)
    {
        var tenantId = GetTenantId();

        // Build dynamic query
        var conditions = new List<string> { "c.tenantId = @tenantId", "c.status = 'Active'" };
        var queryDef = new QueryDefinition("SELECT * FROM c WHERE ");
        queryDef.WithParameter("@tenantId", tenantId);

        if (!string.IsNullOrEmpty(name))
        {
            conditions.Add("(CONTAINS(LOWER(c.firstName), LOWER(@name)) OR CONTAINS(LOWER(c.lastName), LOWER(@name)) OR CONTAINS(LOWER(c.organizationName), LOWER(@name)))");
            queryDef.WithParameter("@name", name);
        }

        if (!string.IsNullOrEmpty(firstName))
        {
            conditions.Add("CONTAINS(LOWER(c.firstName), LOWER(@firstName))");
            queryDef.WithParameter("@firstName", firstName);
        }

        if (!string.IsNullOrEmpty(lastName))
        {
            conditions.Add("CONTAINS(LOWER(c.lastName), LOWER(@lastName))");
            queryDef.WithParameter("@lastName", lastName);
        }

        if (!string.IsNullOrEmpty(specialty))
        {
            conditions.Add("CONTAINS(LOWER(c.primarySpecialty), LOWER(@specialty))");
            queryDef.WithParameter("@specialty", specialty);
        }

        if (!string.IsNullOrEmpty(zipCode))
        {
            conditions.Add("c.zipCode = @zipCode");
            queryDef.WithParameter("@zipCode", zipCode);
        }

        if (!string.IsNullOrEmpty(state))
        {
            conditions.Add("c.state = @state");
            queryDef.WithParameter("@state", state);
        }

        if (!string.IsNullOrEmpty(city))
        {
            conditions.Add("CONTAINS(LOWER(c.city), LOWER(@city))");
            queryDef.WithParameter("@city", city);
        }

        if (providerType.HasValue)
        {
            conditions.Add("c.providerType = @providerType");
            queryDef.WithParameter("@providerType", providerType.Value.ToString());
        }

        if (acceptingNewPatients.HasValue)
        {
            conditions.Add("c.acceptingNewPatients = @acceptingNewPatients");
            queryDef.WithParameter("@acceptingNewPatients", acceptingNewPatients.Value);
        }

        // Network participation filter (array search)
        if (!string.IsNullOrEmpty(planId) || lineOfBusiness.HasValue)
        {
            if (!string.IsNullOrEmpty(planId) && lineOfBusiness.HasValue)
            {
                conditions.Add("EXISTS(SELECT VALUE n FROM n IN c.networkParticipations WHERE n.planId = @planId AND n.lineOfBusiness = @lineOfBusiness)");
                queryDef.WithParameter("@planId", planId);
                queryDef.WithParameter("@lineOfBusiness", lineOfBusiness.Value.ToString());
            }
            else if (!string.IsNullOrEmpty(planId))
            {
                conditions.Add("EXISTS(SELECT VALUE n FROM n IN c.networkParticipations WHERE n.planId = @planId)");
                queryDef.WithParameter("@planId", planId);
            }
            else if (lineOfBusiness.HasValue)
            {
                conditions.Add("EXISTS(SELECT VALUE n FROM n IN c.networkParticipations WHERE n.lineOfBusiness = @lineOfBusiness)");
                queryDef.WithParameter("@lineOfBusiness", lineOfBusiness.Value.ToString());
            }
        }

        var queryText = $"SELECT * FROM c WHERE {string.Join(" AND ", conditions)} ORDER BY c.lastName, c.organizationName OFFSET {(page - 1) * pageSize} LIMIT {pageSize}";
        var finalQuery = new QueryDefinition(queryText);

        // Re-apply all parameters to final query
        foreach (var (name2, value) in queryDef.GetQueryParameters())
        {
            finalQuery.WithParameter(name2, value);
        }

        var iterator = _container.GetItemQueryIterator<Provider>(finalQuery);
        var results = new List<Provider>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results.Select(Hydrate).ToList();
    }

    public async Task<Provider> CreateAsync(Provider provider)
    {
        var tenantId = GetTenantId();
        provider.TenantId = tenantId;

        var response = await _container.CreateItemAsync(provider, new PartitionKey(tenantId));
        return response.Resource;
    }

    public async Task<Provider> UpdateAsync(Provider provider)
    {
        var tenantId = GetTenantId();
        provider.TenantId = tenantId;

        // Reject mutations on non-Draft rows. Hydration normalizes legacy
        // rows to Active, which means updates against legacy data also
        // surface 409 — callers must amend through the new draft path.
        Provider? existing;
        try
        {
            var read = await _container.ReadItemAsync<Provider>(provider.Id, new PartitionKey(tenantId));
            existing = Hydrate(read.Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            existing = null;
        }

        if (existing != null && existing.VersionState != ProviderVersionState.Draft)
        {
            throw new ProviderVersionStateException(
                existing.ProviderId, existing.VersionId, existing.VersionState,
                $"Provider version {existing.VersionId} is {existing.VersionState} and cannot be updated. Create an amendment via POST /amend.");
        }

        var response = await _container.ReplaceItemAsync(
            provider,
            provider.Id,
            new PartitionKey(tenantId));
        return response.Resource;
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();
        await _container.DeleteItemAsync<Provider>(id, new PartitionKey(tenantId));
    }

    public async Task<IReadOnlyList<Provider>> ListNetworkRosterAsync(
        NetworkRosterQuery query,
        NetworkRosterSort sort,
        int skip,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(query.TenantId))
            throw new ArgumentException("NetworkRosterQuery.TenantId is required.", nameof(query));
        if (string.IsNullOrEmpty(query.NetworkId))
            throw new ArgumentException("NetworkRosterQuery.NetworkId is required.", nameof(query));

        // Defensive clamp; the controller already enforces this. Skipping
        // negative offsets prevents accidental SQL injection via the
        // OFFSET/LIMIT literals below (we accept only int values).
        var effectivePageSize = Math.Clamp(query.PageSize, 1, NetworkRosterDefaults.MaxPageSize);
        var safeSkip = Math.Max(skip, 0);
        var asOf = (query.AsOfDate ?? DateTime.UtcNow).ToUniversalTime();

        var parameters = new List<(string Name, object Value)>
        {
            ("@tenantId", query.TenantId),
            ("@networkId", query.NetworkId),
            ("@active", ProviderVersionState.Active.ToString()),
            ("@statusActive", ProviderStatus.Active.ToString()),
            ("@asOf", asOf),
        };

        // Participation-level filters live inside an EXISTS subquery so a
        // single row matches when at least one participation satisfies
        // every supplied filter. Provider-level filters stay on the outer.
        var participationConditions = new List<string>
        {
            "n.networkId = @networkId",
            "(NOT IS_DEFINED(n.effectiveDate) OR n.effectiveDate <= @asOf)",
            "(NOT IS_DEFINED(n.terminationDate) OR n.terminationDate = null OR n.terminationDate >= @asOf)",
        };
        if (query.LineOfBusiness.HasValue)
        {
            participationConditions.Add("n.lineOfBusiness = @lineOfBusiness");
            parameters.Add(("@lineOfBusiness", query.LineOfBusiness.Value.ToString()));
        }
        if (!string.IsNullOrEmpty(query.Tier))
        {
            participationConditions.Add("n.networkTier = @tier");
            parameters.Add(("@tier", query.Tier));
        }
        if (query.AcceptingNewPatients.HasValue)
        {
            participationConditions.Add("n.acceptingNewPatients = @participationAcceptingNew");
            parameters.Add(("@participationAcceptingNew", query.AcceptingNewPatients.Value));
        }

        var existsClause =
            $"EXISTS(SELECT VALUE n FROM n IN c.networkParticipations WHERE {string.Join(" AND ", participationConditions)})";

        var conditions = new List<string>
        {
            "c.tenantId = @tenantId",
            // "Active" matches three shapes (mirrors Hydrate()):
            //   1. versionState == Active (current versioned shape)
            //   2. versionState absent (legacy)
            //   3. versionId missing/null/empty AND status == 'Active' (legacy
            //      row where versionState defaulted to enum-zero on read)
            // Without (3) these legacy rows would be wrongly excluded.
            "(c.versionState = @active OR NOT IS_DEFINED(c.versionState) " +
                "OR ((NOT IS_DEFINED(c.versionId) OR c.versionId = null OR c.versionId = \"\") AND c.status = @statusActive))",
            "(NOT IS_DEFINED(c.terminationDate) OR c.terminationDate = null OR c.terminationDate >= @asOf)",
            existsClause,
        };

        if (!string.IsNullOrEmpty(query.Specialty))
        {
            conditions.Add(
                "(CONTAINS(LOWER(c.primarySpecialty), LOWER(@specialty)) OR CONTAINS(LOWER(c.taxonomyCode), LOWER(@specialty)))");
            parameters.Add(("@specialty", query.Specialty));
        }

        if (query.AcceptingNewPatients.HasValue)
        {
            conditions.Add("c.acceptingNewPatients = @providerAcceptingNew");
            parameters.Add(("@providerAcceptingNew", query.AcceptingNewPatients.Value));
        }

        var orderBy = sort switch
        {
            NetworkRosterSort.NameDesc =>
                "ORDER BY c.lastName DESC, c.organizationName DESC, c.id DESC",
            NetworkRosterSort.IntegrityScoreDesc =>
                // Cosmos can store integrityScore as null (field present
                // but null) or absent entirely. IS_DEFINED returns 1 for
                // both cases when null; IS_NUMBER returns true only for
                // actual numeric values so providers with null or missing
                // scores get hasScore=0 and sort last — nulls-last before
                // the OFFSET/LIMIT clause.
                "ORDER BY (IS_NUMBER(c.integrityScore) ? 1 : 0) DESC, c.integrityScore DESC, c.id ASC",
            _ =>
                "ORDER BY c.lastName ASC, c.organizationName ASC, c.id ASC",
        };

        var sql =
            "SELECT * FROM c WHERE " + string.Join(" AND ", conditions) + " " +
            orderBy + " " +
            $"OFFSET {safeSkip} LIMIT {effectivePageSize}";

        var queryDef = new QueryDefinition(sql);
        foreach (var (name, value) in parameters)
        {
            queryDef = queryDef.WithParameter(name, value);
        }

        var iterator = _container.GetItemQueryIterator<Provider>(
            queryDef,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(query.TenantId),
                MaxItemCount = effectivePageSize,
            });

        var results = new List<Provider>(effectivePageSize);
        while (iterator.HasMoreResults)
        {
            ct.ThrowIfCancellationRequested();
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page.Select(Hydrate));
            if (results.Count >= effectivePageSize) break;
        }

        return results;
    }

    public async Task<Provider?> GetLatestActiveAsync(string providerId, DateTime asOf)
    {
        var tenantId = GetTenantId();

        // Hydration rule: rows missing versionState are treated as Active
        // (legacy data). The query also accepts legacy rows where
        // providerId is unset by falling back to the row's own id.
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.tenantId = @tenantId AND " +
                "(c.providerId = @providerId OR (NOT IS_DEFINED(c.providerId) AND c.id = @providerId)) AND " +
                "(NOT IS_DEFINED(c.versionState) OR c.versionState = @active) AND " +
                "(NOT IS_DEFINED(c.terminationDate) OR c.terminationDate = null OR c.terminationDate >= @asOf) " +
                "ORDER BY c.versionNumber DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@providerId", providerId)
            .WithParameter("@active", ProviderVersionState.Active.ToString())
            .WithParameter("@asOf", asOf);

        var iterator = _container.GetItemQueryIterator<Provider>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            var first = page.FirstOrDefault();
            if (first != null) return Hydrate(first);
        }
        return null;
    }

    public async Task<Provider?> GetVersionAsync(string providerId, string versionId)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.tenantId = @tenantId AND " +
                "(c.providerId = @providerId OR (NOT IS_DEFINED(c.providerId) AND c.id = @providerId)) AND " +
                "c.versionId = @versionId")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@providerId", providerId)
            .WithParameter("@versionId", versionId);

        var iterator = _container.GetItemQueryIterator<Provider>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            var first = page.FirstOrDefault();
            if (first != null) return Hydrate(first);
        }
        return null;
    }

    public async Task<(IReadOnlyList<Provider> Items, string? ContinuationToken)> ListVersionsAsync(
        string providerId, int pageSize, string? continuationToken)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.tenantId = @tenantId AND " +
                "(c.providerId = @providerId OR (NOT IS_DEFINED(c.providerId) AND c.id = @providerId)) " +
                "ORDER BY c.versionNumber DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@providerId", providerId);

        var requestOptions = new QueryRequestOptions
        {
            PartitionKey = new PartitionKey(tenantId),
            MaxItemCount = pageSize
        };

        var iterator = _container.GetItemQueryIterator<Provider>(query, continuationToken, requestOptions);
        if (!iterator.HasMoreResults)
            return (Array.Empty<Provider>(), null);

        var response = await iterator.ReadNextAsync();
        var items = response.Select(Hydrate).ToList();
        return (items, response.ContinuationToken);
    }

    public async Task<Provider> CreateDraftAsync(Provider draft)
    {
        var tenantId = GetTenantId();
        draft.TenantId = tenantId;
        draft.VersionState = ProviderVersionState.Draft;
        if (string.IsNullOrEmpty(draft.Id)) draft.Id = Guid.NewGuid().ToString();
        if (string.IsNullOrEmpty(draft.ProviderId)) draft.ProviderId = draft.Id;
        draft.LastUpdatedDate = DateTime.UtcNow;

        var response = await _container.CreateItemAsync(draft, new PartitionKey(tenantId));
        return response.Resource;
    }

    public async Task<Provider> UpdateDraftAsync(Provider draft)
    {
        Provider? existing;
        try
        {
            var read = await _container.ReadItemAsync<Provider>(draft.Id, new PartitionKey(draft.TenantId));
            existing = Hydrate(read.Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            existing = null;
        }
        if (existing == null)
        {
            throw new ProviderVersionStateException(draft.ProviderId, draft.VersionId, ProviderVersionState.Draft,
                $"Draft {draft.VersionId} not found") { IsNotFound = true };
        }
        if (existing.VersionState != ProviderVersionState.Draft)
        {
            throw new ProviderVersionStateException(
                existing.ProviderId, existing.VersionId, existing.VersionState,
                $"Provider version {existing.VersionId} is {existing.VersionState} and cannot be edited.");
        }

        draft.LastUpdatedDate = DateTime.UtcNow;
        draft.VersionState = ProviderVersionState.Draft;

        var response = await _container.ReplaceItemAsync(draft, draft.Id, new PartitionKey(draft.TenantId));
        return response.Resource;
    }

    public async Task<Provider> ActivateAndSupersedeAsync(Provider draftToActivate, Provider? predecessor)
    {
        if (draftToActivate.VersionState != ProviderVersionState.Active)
        {
            throw new InvalidOperationException(
                "ActivateAndSupersedeAsync expects draftToActivate to already have VersionState=Active applied by the service layer.");
        }

        draftToActivate.LastUpdatedDate = DateTime.UtcNow;

        var batch = _container.CreateTransactionalBatch(new PartitionKey(draftToActivate.TenantId))
            .ReplaceItem(draftToActivate.Id, draftToActivate);

        if (predecessor != null)
        {
            predecessor.LastUpdatedDate = DateTime.UtcNow;
            batch = batch.ReplaceItem(predecessor.Id, predecessor);
        }

        using var response = await batch.ExecuteAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new ProviderVersionStateException(
                draftToActivate.Id, draftToActivate.VersionId, draftToActivate.VersionState,
                $"Atomic activate/supersede failed: {response.StatusCode}");
        }

        return draftToActivate;
    }

    public async Task<Provider> ReplaceVersionRowAsync(Provider version)
    {
        version.LastUpdatedDate = DateTime.UtcNow;
        var response = await _container.ReplaceItemAsync(
            version,
            version.Id,
            new PartitionKey(version.TenantId));
        return response.Resource;
    }

    public async Task<bool> UpdateIntegrityProjectionAsync(
        string tenantId,
        string providerId,
        int? integrityScore,
        string? integrityRating,
        DateTimeOffset? lastVerifiedAt,
        DateTimeOffset? nextVerificationDue,
        CancellationToken ct = default)
    {
        // Resolve the head Active row by chain key. Cosmos PatchItemAsync
        // is keyed on the per-row document Id, so we have to look up the
        // row id first. The lookup query is partition-scoped.
        //
        // Hydration rule (mirrors Hydrate()) — three "Active" shapes,
        // each Status-gated to keep legacy Terminated/Suspended rows
        // out:
        //   1. versionState = Active.
        //   2. versionState undefined AND status = Active.
        //   3. versionId undefined/null/empty AND status = Active.
        // See docs/architecture/provider-versioning.md "Legacy
        // hydration query pattern".
        var query = new QueryDefinition(
                "SELECT TOP 1 c.id FROM c WHERE c.tenantId = @tenantId AND " +
                "(c.providerId = @providerId OR (NOT IS_DEFINED(c.providerId) AND c.id = @providerId)) AND " +
                "(c.versionState = @active " +
                "OR (NOT IS_DEFINED(c.versionState) AND c.status = @statusActive) " +
                "OR ((NOT IS_DEFINED(c.versionId) OR c.versionId = null OR c.versionId = \"\") AND c.status = @statusActive)) " +
                "ORDER BY c.versionNumber DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@providerId", providerId)
            .WithParameter("@active", ProviderVersionState.Active.ToString())
            .WithParameter("@statusActive", ProviderStatus.Active.ToString());

        string? rowId = null;
        var iterator = _container.GetItemQueryIterator<HeadIdResult>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        if (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            rowId = page.FirstOrDefault()?.Id;
        }

        if (string.IsNullOrEmpty(rowId)) return false;

        var ops = new List<PatchOperation>
        {
            PatchOperation.Set("/integrityScore", integrityScore),
            PatchOperation.Set("/integrityRating", integrityRating),
            PatchOperation.Set("/lastVerifiedAt", lastVerifiedAt),
            PatchOperation.Set("/nextVerificationDue", nextVerificationDue),
            PatchOperation.Set("/lastUpdatedDate", DateTime.UtcNow),
        };

        try
        {
            await _container.PatchItemAsync<Provider>(
                rowId,
                new PartitionKey(tenantId),
                ops,
                cancellationToken: ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Row was deleted between the lookup and the patch.
            return false;
        }
    }

    public async Task<bool> UpdateCredentialingProjectionAsync(
        string tenantId,
        string providerId,
        CredentialingStatus status,
        DateTime? credentialingDate,
        DateTime? recredentialingDueDate,
        CancellationToken ct = default)
    {
        // Mirror UpdateIntegrityProjectionAsync: lookup head Active row by
        // chain key, patch only the three credentialing projection fields
        // via PatchItemAsync. No version-state guard. Hydration rule
        // (three "Active" shapes, each Status-gated) is identical.
        var query = new QueryDefinition(
                "SELECT TOP 1 c.id FROM c WHERE c.tenantId = @tenantId AND " +
                "(c.providerId = @providerId OR (NOT IS_DEFINED(c.providerId) AND c.id = @providerId)) AND " +
                "(c.versionState = @active " +
                "OR (NOT IS_DEFINED(c.versionState) AND c.status = @statusActive) " +
                "OR ((NOT IS_DEFINED(c.versionId) OR c.versionId = null OR c.versionId = \"\") AND c.status = @statusActive)) " +
                "ORDER BY c.versionNumber DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@providerId", providerId)
            .WithParameter("@active", ProviderVersionState.Active.ToString())
            .WithParameter("@statusActive", ProviderStatus.Active.ToString());

        string? rowId = null;
        var iterator = _container.GetItemQueryIterator<HeadIdResult>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        if (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            rowId = page.FirstOrDefault()?.Id;
        }

        if (string.IsNullOrEmpty(rowId)) return false;

        // Patch the enum value directly so the Cosmos SDK serializer
        // chooses the same representation it uses for full-document
        // writes. Calling .ToString() would force a string here even
        // if the rest of the document stored the field differently —
        // a future serializer-config change would silently diverge.
        var ops = new List<PatchOperation>
        {
            PatchOperation.Set("/credentialingStatus", status),
            PatchOperation.Set("/credentialingDate", credentialingDate),
            PatchOperation.Set("/recredentialingDueDate", recredentialingDueDate),
            PatchOperation.Set("/lastUpdatedDate", DateTime.UtcNow),
        };

        try
        {
            await _container.PatchItemAsync<Provider>(
                rowId,
                new PartitionKey(tenantId),
                ops,
                cancellationToken: ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<Provider>> ListProvidersForIntegrityRefreshAsync(
        string tenantId,
        DateTimeOffset dueBefore,
        bool includeNeverVerified,
        int skip,
        int pageSize,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tenantId))
            throw new ArgumentException("tenantId is required.", nameof(tenantId));
        var safeSkip = Math.Max(skip, 0);
        var safePageSize = Math.Clamp(pageSize, 1, 1000);

        // Active head only — projection only writes to Active rows.
        // Hydration rule (mirrors Hydrate()) — three "Active" shapes,
        // each Status-gated to keep legacy Terminated/Suspended rows
        // out of refresh batches:
        //   1. versionState = Active.
        //   2. versionState undefined AND status = Active.
        //   3. versionId undefined/null/empty AND status = Active.
        // See docs/architecture/provider-versioning.md "Legacy
        // hydration query pattern".
        var sql = "SELECT * FROM c WHERE c.tenantId = @tenantId AND " +
                  "(c.versionState = @active " +
                  "OR (NOT IS_DEFINED(c.versionState) AND c.status = @statusActive) " +
                  "OR ((NOT IS_DEFINED(c.versionId) OR c.versionId = null OR c.versionId = \"\") AND c.status = @statusActive)) AND ";
        sql += includeNeverVerified
            ? "(NOT IS_DEFINED(c.nextVerificationDue) OR c.nextVerificationDue = null OR c.nextVerificationDue <= @dueBefore) "
            : "(IS_DEFINED(c.nextVerificationDue) AND c.nextVerificationDue != null AND c.nextVerificationDue <= @dueBefore) ";
        // Secondary sort on c.id keeps OFFSET/LIMIT pagination
        // deterministic when multiple rows share the same providerId
        // (legacy single-row chains where providerId may be empty).
        sql += $"ORDER BY c.providerId ASC, c.id ASC OFFSET {safeSkip} LIMIT {safePageSize}";

        var query = new QueryDefinition(sql)
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@active", ProviderVersionState.Active.ToString())
            .WithParameter("@statusActive", ProviderStatus.Active.ToString())
            .WithParameter("@dueBefore", dueBefore);

        var iterator = _container.GetItemQueryIterator<Provider>(
            query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(tenantId),
                MaxItemCount = safePageSize,
            });

        var results = new List<Provider>(safePageSize);
        while (iterator.HasMoreResults)
        {
            ct.ThrowIfCancellationRequested();
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page.Select(Hydrate));
            if (results.Count >= safePageSize) break;
        }
        return results;
    }

    public async Task<IReadOnlyList<string>> ListProviderTenantIdsAsync(CancellationToken ct = default)
    {
        // Cross-partition; Cosmos DISTINCT VALUE is supported on simple
        // scalars. The hosted worker calls this once per sweep so the
        // RU cost is bounded by the sweep cadence.
        var query = new QueryDefinition("SELECT DISTINCT VALUE c.tenantId FROM c");
        var iterator = _container.GetItemQueryIterator<string>(query);
        var tenants = new HashSet<string>(StringComparer.Ordinal);
        while (iterator.HasMoreResults)
        {
            ct.ThrowIfCancellationRequested();
            var page = await iterator.ReadNextAsync(ct);
            foreach (var t in page)
            {
                if (!string.IsNullOrEmpty(t)) tenants.Add(t);
            }
        }
        return tenants.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    public async Task<long> CountStaleProvidersAsync(
        string tenantId,
        DateTimeOffset staleBefore,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tenantId))
            throw new ArgumentException("tenantId is required.", nameof(tenantId));

        // Hydration rule (mirrors Hydrate()) — three "Active" shapes,
        // each Status-gated, identical to the refresh-list query above.
        // A provider is stale when LastVerifiedAt is missing/null or
        // older than staleBefore.
        var sql = "SELECT VALUE COUNT(1) FROM c WHERE c.tenantId = @tenantId AND " +
                  "(c.versionState = @active " +
                  "OR (NOT IS_DEFINED(c.versionState) AND c.status = @statusActive) " +
                  "OR ((NOT IS_DEFINED(c.versionId) OR c.versionId = null OR c.versionId = \"\") AND c.status = @statusActive)) AND " +
                  "(NOT IS_DEFINED(c.lastVerifiedAt) OR c.lastVerifiedAt = null OR c.lastVerifiedAt < @staleBefore)";

        var query = new QueryDefinition(sql)
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@active", ProviderVersionState.Active.ToString())
            .WithParameter("@statusActive", ProviderStatus.Active.ToString())
            .WithParameter("@staleBefore", staleBefore);

        var iterator = _container.GetItemQueryIterator<long>(
            query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(tenantId),
            });

        long total = 0;
        while (iterator.HasMoreResults)
        {
            ct.ThrowIfCancellationRequested();
            var page = await iterator.ReadNextAsync(ct);
            foreach (var v in page) total += v;
        }
        return total;
    }

    public async Task<bool> UpdatePanelGatingDefaultsAsync(
        string tenantId,
        string providerId,
        int participationIndex,
        PanelGatingFields fields,
        CancellationToken ct = default)
    {
        if (participationIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(participationIndex));
        if (fields == null) throw new ArgumentNullException(nameof(fields));

        // Resolve the head Active row by chain key + read its etag so we
        // can issue a conditional patch. Mirror UpdateIntegrityProjectionAsync
        // hydration rule: three Active shapes, each Status-gated.
        //
        // The Cosmos SDK default serializer (Newtonsoft.Json) is
        // case-insensitive on property matching but does not honor
        // System.Text.Json attributes — so we alias `_etag` (which is
        // not a valid C# identifier prefix) to `etag` in the projection
        // to keep HeadRowSnapshot attribute-free.
        var query = new QueryDefinition(
                "SELECT TOP 1 c.id, c._etag AS etag, c.networkParticipations FROM c WHERE c.tenantId = @tenantId AND " +
                "(c.providerId = @providerId OR (NOT IS_DEFINED(c.providerId) AND c.id = @providerId)) AND " +
                "(c.versionState = @active " +
                "OR (NOT IS_DEFINED(c.versionState) AND c.status = @statusActive) " +
                "OR ((NOT IS_DEFINED(c.versionId) OR c.versionId = null OR c.versionId = \"\") AND c.status = @statusActive)) " +
                "ORDER BY c.versionNumber DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@providerId", providerId)
            .WithParameter("@active", ProviderVersionState.Active.ToString())
            .WithParameter("@statusActive", ProviderStatus.Active.ToString());

        HeadRowSnapshot? head = null;
        var iterator = _container.GetItemQueryIterator<HeadRowSnapshot>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        if (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            head = page.FirstOrDefault();
        }

        if (head == null || string.IsNullOrEmpty(head.Id)) return false;

        // Bounds check the participation index against the read-side
        // document. A page entry that's been mutated between the iterator
        // read and the patch (concurrent CRUD) may have fewer
        // participations than expected — skip rather than corrupt.
        var participations = head.NetworkParticipations ?? new List<NetworkParticipation>();
        if (participationIndex >= participations.Count) return false;

        var ops = new List<PatchOperation>
        {
            PatchOperation.Set($"/networkParticipations/{participationIndex}/panelLimit",
                fields.PanelLimit),
            PatchOperation.Set($"/networkParticipations/{participationIndex}/panelAccepted",
                fields.PanelAccepted),
            PatchOperation.Set($"/networkParticipations/{participationIndex}/acceptedLobs",
                fields.AcceptedLobs.ToList()),
            PatchOperation.Set($"/networkParticipations/{participationIndex}/minAcceptedAgeYears",
                fields.MinAcceptedAgeYears),
            PatchOperation.Set($"/networkParticipations/{participationIndex}/maxAcceptedAgeYears",
                fields.MaxAcceptedAgeYears),
            PatchOperation.Set("/lastUpdatedDate", DateTime.UtcNow),
        };

        // Conditional patch via IfMatchEtag — a concurrent CRUD write
        // bumps the etag and the conditional patch returns 412 Precondition
        // Failed. The service layer counts that as an etag conflict and
        // moves on; the operator can rerun the backfill to pick up the
        // skipped row.
        var patchOptions = new PatchItemRequestOptions
        {
            IfMatchEtag = head.Etag,
        };

        try
        {
            await _container.PatchItemAsync<Provider>(
                head.Id,
                new PartitionKey(tenantId),
                ops,
                patchOptions,
                cancellationToken: ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound
                                          || ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            // NotFound: row was deleted between the lookup and the patch.
            // PreconditionFailed: a concurrent write moved the etag —
            // the service layer treats both as a skip.
            return false;
        }
    }

    public async Task<IReadOnlyList<Provider>> ListProvidersForPanelGatingBackfillAsync(
        string tenantId,
        int skip,
        int pageSize,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tenantId))
            throw new ArgumentException("tenantId is required.", nameof(tenantId));
        var safeSkip = Math.Max(skip, 0);
        var safePageSize = Math.Clamp(pageSize, 1, 1000);

        // Hydration rule (mirrors Hydrate()) — three "Active" shapes,
        // each Status-gated. Storage-layer eligibility is a superset
        // filter: any row that has at least one participation with
        // PanelLimit unset is a candidate. The service-layer eligibility
        // check (PanelGatingFields.IsAtTypeDefaults) is the
        // authoritative filter — a false-positive page entry is just a
        // no-op skip.
        //
        // We can't easily AND-filter all five fields in a single Cosmos
        // SQL query without building a complex nested EXISTS over the
        // participations array. The current shape (any participation
        // with PanelLimit unset) catches the vast majority of legacy
        // rows; the service layer handles the rest.
        var sql = "SELECT * FROM c WHERE c.tenantId = @tenantId AND " +
                  "(c.versionState = @active " +
                  "OR (NOT IS_DEFINED(c.versionState) AND c.status = @statusActive) " +
                  "OR ((NOT IS_DEFINED(c.versionId) OR c.versionId = null OR c.versionId = \"\") AND c.status = @statusActive)) AND " +
                  "EXISTS(SELECT VALUE p FROM p IN c.networkParticipations WHERE " +
                  "  (NOT IS_DEFINED(p.panelLimit) OR p.panelLimit = null) AND " +
                  "  (NOT IS_DEFINED(p.panelAccepted) OR p.panelAccepted = null) AND " +
                  "  (NOT IS_DEFINED(p.acceptedLobs) OR p.acceptedLobs = null OR ARRAY_LENGTH(p.acceptedLobs) = 0) AND " +
                  "  (NOT IS_DEFINED(p.minAcceptedAgeYears) OR p.minAcceptedAgeYears = null) AND " +
                  "  (NOT IS_DEFINED(p.maxAcceptedAgeYears) OR p.maxAcceptedAgeYears = null)) " +
                  $"ORDER BY c.providerId ASC, c.id ASC OFFSET {safeSkip} LIMIT {safePageSize}";

        var query = new QueryDefinition(sql)
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@active", ProviderVersionState.Active.ToString())
            .WithParameter("@statusActive", ProviderStatus.Active.ToString());

        var iterator = _container.GetItemQueryIterator<Provider>(
            query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(tenantId),
                MaxItemCount = safePageSize,
            });

        var results = new List<Provider>(safePageSize);
        while (iterator.HasMoreResults)
        {
            ct.ThrowIfCancellationRequested();
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page.Select(Hydrate));
            if (results.Count >= safePageSize) break;
        }
        return results;
    }

    private sealed class HeadIdResult
    {
        public string Id { get; set; } = string.Empty;
    }

    private sealed class HeadRowSnapshot
    {
        // Cosmos SDK default serializer (Newtonsoft.Json) matches
        // property names case-insensitively, so PascalCase here lines up
        // with camelCase JSON. `_etag` cannot be a C# property name, so
        // the SELECT clause aliases it to `etag` (see callers).
        public string Id { get; set; } = string.Empty;
        public string? Etag { get; set; }
        public List<NetworkParticipation>? NetworkParticipations { get; set; }
    }

    /// <summary>
    /// Backfill identity fields on legacy rows that predate this feature
    /// and keep the legacy <see cref="Provider.Status"/> in sync with
    /// <see cref="Provider.VersionState"/> so existing consumers (search
    /// filter, PcpAssignmentService) keep working unchanged.
    /// </summary>
    private static Provider Hydrate(Provider provider)
    {
        if (string.IsNullOrEmpty(provider.ProviderId))
        {
            // Legacy single-row chain: the document Id is also the chain key.
            provider.ProviderId = provider.Id;
        }

        if (string.IsNullOrEmpty(provider.VersionId))
        {
            provider.VersionId = provider.Id;
            provider.VersionNumber = provider.VersionNumber <= 0 ? 1 : provider.VersionNumber;
            // Map the legacy ProviderStatus onto the version state so
            // pre-existing rows hydrate with a sensible state.
            provider.VersionState = provider.Status switch
            {
                ProviderStatus.Terminated => ProviderVersionState.Terminated,
                ProviderStatus.Inactive => ProviderVersionState.Suspended,
                ProviderStatus.Pending => ProviderVersionState.Draft,
                _ => ProviderVersionState.Active
            };
        }

        // Keep Status synced with VersionState for downstream consumers.
        provider.Status = provider.VersionState switch
        {
            ProviderVersionState.Active => ProviderStatus.Active,
            ProviderVersionState.Suspended => ProviderStatus.Inactive,
            ProviderVersionState.Terminated => ProviderStatus.Terminated,
            ProviderVersionState.Superseded => ProviderStatus.Inactive,
            ProviderVersionState.Draft => ProviderStatus.Pending,
            _ => provider.Status
        };

        return provider;
    }
}

// Extension method to get query parameters (for debugging/logging)
public static class QueryDefinitionExtensions
{
    public static IEnumerable<(string, object)> GetQueryParameters(this QueryDefinition queryDef)
    {
        // Note: QueryDefinition doesn't expose parameters publicly
        // This is a placeholder - in production, track parameters separately or use logging
        return new List<(string, object)>();
    }
}
