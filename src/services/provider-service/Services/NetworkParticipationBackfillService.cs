using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.Extensions.Options;
using ProviderService.Models;
using ProviderService.Repositories;

namespace ProviderService.Services;

/// <summary>
/// Drives the one-time panel-gating backfill across a single tenant's
/// providers (capability 5.5).
///
/// <list type="number">
///   <item>Resolve eligible providers in a tenant via
///     <see cref="IProviderRepository.ListProvidersForPanelGatingBackfillAsync"/>
///     (storage-layer superset filter).</item>
///   <item>For each provider's <see cref="Provider.NetworkParticipations"/>,
///     identify slots where every panel-gating field is at its type
///     default (authoritative service-layer eligibility).</item>
///   <item>Patch each eligible slot via
///     <see cref="IProviderRepository.UpdatePanelGatingDefaultsAsync"/>
///     (positional, conditional, version-state-bypassing).</item>
///   <item>Emit a deterministic <c>PanelGatingBackfilled</c> event per
///     patched slot via
///     <see cref="INetworkParticipationEventPublisher"/>.</item>
/// </list>
///
/// <para>
/// Failure isolation: a patch failure on one row does not abort the
/// run. Etag conflicts are counted separately so operators can decide
/// whether to rerun. Event publication is best-effort — the patch is
/// the source of truth.
/// </para>
/// </summary>
public interface INetworkParticipationBackfillService
{
    Task<NetworkParticipationBackfillResult> RunTenantAsync(
        string tenantId,
        NetworkParticipationBackfillRequest request,
        CancellationToken ct = default);
}

public sealed class NetworkParticipationBackfillService : INetworkParticipationBackfillService
{
    private readonly IProviderRepository _providers;
    private readonly INetworkParticipationEventPublisher _events;
    private readonly IOptions<NetworkParticipationBackfillOptions> _options;
    private readonly ILogger<NetworkParticipationBackfillService> _logger;

    public NetworkParticipationBackfillService(
        IProviderRepository providers,
        INetworkParticipationEventPublisher events,
        IOptions<NetworkParticipationBackfillOptions> options,
        ILogger<NetworkParticipationBackfillService> logger)
    {
        _providers = providers;
        _events = events;
        _options = options;
        _logger = logger;
    }

    public async Task<NetworkParticipationBackfillResult> RunTenantAsync(
        string tenantId,
        NetworkParticipationBackfillRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tenantId))
            throw new ArgumentException("tenantId is required.", nameof(tenantId));

        var opts = _options.Value;
        var pageSize = Math.Clamp(request.PageSize ?? opts.PageSize, 1, 1000);
        var maxProviders = request.MaxProviders ?? opts.MaxProvidersPerCall;
        var backfillRunId = ProviderVersionId.NewId();
        var defaults = PanelGatingFields.LegacyUnconstrained();

        var result = new NetworkParticipationBackfillResult
        {
            TenantId = tenantId,
            BackfillRunId = backfillRunId,
            StartedAt = DateTimeOffset.UtcNow,
        };

        _logger.LogInformation(
            "panel-gating backfill run starting tenant={Tenant} runId={RunId} pageSize={PageSize} maxProviders={Max}",
            Sanitize(tenantId), backfillRunId, pageSize, maxProviders);

        var skip = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (maxProviders.HasValue && result.ProvidersInspected >= maxProviders.Value) break;

            var page = await _providers.ListProvidersForPanelGatingBackfillAsync(
                tenantId, skip, pageSize, ct);
            if (page.Count == 0) break;

            foreach (var provider in page)
            {
                if (maxProviders.HasValue && result.ProvidersInspected >= maxProviders.Value) break;
                result.ProvidersInspected++;

                await ProcessProviderAsync(tenantId, provider, defaults, backfillRunId, request, result, ct);
            }

            // Fixed-step pagination relies on the eligible provider
            // set being stable WITHIN a single run — i.e., this run's
            // own patches don't shrink the set out from under the
            // iterator. Because the backfill writes the panel-gating
            // fields to their type defaults (LegacyUnconstrained), and
            // eligibility is defined as "all five fields at type
            // defaults," a patched row is still considered eligible by
            // the storage-layer superset filter. Patched rows therefore
            // do NOT drop out of subsequent pages, and skip-by-page is
            // safe. (Across separate operator-triggered runs, eligible
            // rows can re-appear; that's a documented rerun behavior,
            // not a single-run iteration bug.)
            skip += page.Count;
            if (page.Count < pageSize) break;
        }

        result.CompletedAt = DateTimeOffset.UtcNow;

        _logger.LogInformation(
            "panel-gating backfill run complete tenant={Tenant} runId={RunId} providersInspected={ProvIn} participationsInspected={PartIn} backfilled={Bf} skipped={Sk} failed={Fa} etagConflicts={Etag}",
            Sanitize(tenantId), backfillRunId,
            result.ProvidersInspected, result.ParticipationsInspected,
            result.ParticipationsBackfilled, result.ParticipationsSkipped,
            result.ParticipationsFailed, result.EtagConflicts);

        return result;
    }

    private async Task ProcessProviderAsync(
        string tenantId,
        Provider provider,
        PanelGatingFields defaults,
        string backfillRunId,
        NetworkParticipationBackfillRequest request,
        NetworkParticipationBackfillResult result,
        CancellationToken ct)
    {
        var participations = provider.NetworkParticipations;
        if (participations == null || participations.Count == 0) return;

        for (var i = 0; i < participations.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var p = participations[i];
            result.ParticipationsInspected++;

            // Authoritative service-layer eligibility — the storage
            // filter is a superset (any-field-unset). A participation
            // that has at least one field already populated is
            // considered "touched" and skipped.
            if (!PanelGatingFields.IsAtTypeDefaults(p))
            {
                result.ParticipationsSkipped++;
                ChoMetrics.NetworkParticipationBackfillOutcomes.Add(1,
                    new KeyValuePair<string, object?>("cho.outcome", "skipped"),
                    new KeyValuePair<string, object?>("cho.tenant_id", tenantId));
                continue;
            }

            bool patched;
            try
            {
                patched = await _providers.UpdatePanelGatingDefaultsAsync(
                    tenantId, provider.ProviderId, i, defaults, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "panel-gating patch failed tenant={Tenant} providerId={ProviderId} index={Index}",
                    Sanitize(tenantId), Sanitize(provider.ProviderId), i);
                result.ParticipationsFailed++;
                ChoMetrics.NetworkParticipationBackfillOutcomes.Add(1,
                    new KeyValuePair<string, object?>("cho.outcome", "failed"),
                    new KeyValuePair<string, object?>("cho.tenant_id", tenantId));
                continue;
            }

            if (!patched)
            {
                // Repository returns false on NotFound (row deleted
                // between read and patch) or PreconditionFailed (etag
                // conflict). Distinguishing the two is not actionable
                // at runtime — both classify as "the row moved
                // underneath us, rerun the operation." Counted as
                // EtagConflicts so the operator metric is conservative.
                result.EtagConflicts++;
                ChoMetrics.NetworkParticipationBackfillOutcomes.Add(1,
                    new KeyValuePair<string, object?>("cho.outcome", "etag_conflict"),
                    new KeyValuePair<string, object?>("cho.tenant_id", tenantId));
                continue;
            }

            result.ParticipationsBackfilled++;
            ChoMetrics.NetworkParticipationBackfillOutcomes.Add(1,
                new KeyValuePair<string, object?>("cho.outcome", "patched"),
                new KeyValuePair<string, object?>("cho.tenant_id", tenantId));

            try
            {
                await _events.PublishPanelGatingBackfilledAsync(
                    tenantId,
                    provider.ProviderId,
                    i,
                    p.PlanId,
                    p.NetworkId,
                    p.LineOfBusiness,
                    backfillRunId,
                    request.ActorId,
                    request.CorrelationId,
                    ct);
            }
            catch (Exception ex)
            {
                // Event publication is best-effort. The patch already
                // landed; the data shape is value-preserving, so a
                // rerun re-applies the same defaults safely. Reruns
                // produce a NEW backfillRunId and therefore a distinct
                // event — by design (see
                // docs/architecture/network-participation-backfill.md
                // "Rerun behavior").
                _logger.LogWarning(ex,
                    "panel-gating-backfilled event publication failed tenant={Tenant} providerId={ProviderId} index={Index} runId={RunId}; patch already applied",
                    Sanitize(tenantId), Sanitize(provider.ProviderId), i, backfillRunId);
            }
        }
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}

/// <summary>
/// Request shape for a single backfill run. Mirrors
/// <see cref="IntegrityProjectionTenantSweepRequest"/>: optional caller
/// overrides for page size + cap, optional actor + correlation id for
/// audit stamping.
/// </summary>
public sealed class NetworkParticipationBackfillRequest
{
    public int? PageSize { get; set; }
    public int? MaxProviders { get; set; }
    public string? ActorId { get; set; }
    public string? CorrelationId { get; set; }
}

/// <summary>
/// Telemetry surfaced by a single backfill run. Returned in the admin
/// endpoint HTTP response so operators can confirm the run; logged at
/// run-start and run-complete.
/// </summary>
public sealed class NetworkParticipationBackfillResult
{
    public string TenantId { get; set; } = string.Empty;
    public string BackfillRunId { get; set; } = string.Empty;
    public int ProvidersInspected { get; set; }
    public int ParticipationsInspected { get; set; }
    public int ParticipationsBackfilled { get; set; }
    public int ParticipationsSkipped { get; set; }
    public int ParticipationsFailed { get; set; }
    public int EtagConflicts { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
}
