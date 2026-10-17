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
    /// <c>SupersededByVersionId = draftToActivate.VersionId</c>. The Mongo
    /// implementation uses a session transaction when the deployment supports
    /// it; otherwise it falls back to sequential writes and logs a
    /// compensating-action warning.
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
    /// implemented as a separate write path: the Mongo impl uses
    /// <c>UpdateOneAsync</c> with <c>$set</c> on the four field paths
    /// only, never <see cref="UpdateAsync"/>'s version-state guard.
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
    /// scoped so the gauge update stays cheap on the worker hot-path.
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
    /// separate write path: the Mongo impl uses
    /// <c>FindOneAndUpdateAsync</c> with <c>$set</c> on the positional
    /// participation slot (FindOneAndUpdate is required because the write
    /// sorts by <c>VersionNumber</c> descending to hit the head when
    /// historical Superseded rows exist). It never goes through
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
    /// implemented as a separate write path: the Mongo impl uses
    /// <c>FindOneAndUpdateAsync</c> with <c>$set</c> on the three field
    /// paths only, never <see cref="UpdateAsync"/>'s version-state guard.
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
