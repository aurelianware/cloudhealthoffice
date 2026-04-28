using BenefitPlanService.Models;
using BenefitPlanService.Repositories;
using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.Extensions.Options;

namespace BenefitPlanService.Services;

/// <summary>
/// One-shot, operator-triggered, per-tenant backfill that populates
/// <see cref="NetworkTier.NetworkId"/> on existing benefit plans
/// (capability 5.5 — NetworkTier as Reference to Organization).
///
/// <para>
/// Operator-driven mapping (Decision 5b): the request body carries an
/// explicit <c>(planId, tierName) → networkId</c> dictionary. The
/// service does not auto-resolve from any embedded NPI snapshot —
/// since <see cref="NetworkTier.ProviderNpis"/> was never consulted by
/// any production code path, treating it as an authoritative auto-map
/// source would compound any seeded-but-stale data the operator may
/// already have in the tenant. See
/// <c>docs/architecture/network-tier-organization-reference.md</c>
/// for the rationale and rollback posture.
/// </para>
///
/// <para>
/// Idempotency: the patch is only applied when the head version's tier
/// has a null/empty <see cref="NetworkTier.NetworkId"/> — re-running
/// the same request is a no-op on already-mapped tiers and produces
/// counter increments under <c>cho.outcome=skipped</c>. Each successful
/// patch validates the supplied <c>networkId</c> against
/// provider-service via <see cref="IOrganizationLookupClient"/>
/// before writing; an unresolved id is recorded under
/// <c>cho.outcome=unresolved</c> and not written.
/// </para>
/// </summary>
public interface INetworkTierBackfillService
{
    Task<NetworkTierBackfillResult> RunTenantAsync(
        string tenantId,
        NetworkTierBackfillRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// Operator-supplied backfill request body. Each entry is one
/// <c>(planId, tierName) → networkId</c> mapping. A plan + tier pair
/// not present in <see cref="Mappings"/> is left untouched (the
/// operator chose not to map it this run).
/// </summary>
public sealed class NetworkTierBackfillRequest
{
    /// <summary>
    /// Operator-supplied mappings. Keyed by <c>(planId, tierName)</c>;
    /// the value is the <c>Organization.OrganizationId</c> chain key in
    /// provider-service.
    /// </summary>
    public List<NetworkTierBackfillMapping> Mappings { get; set; } = new();

    /// <summary>
    /// Optional actor id (audit log only). Resolved from the request
    /// principal at the controller boundary; defaults to a synthetic
    /// label when no principal is available.
    /// </summary>
    public string? ActorId { get; set; }

    /// <summary>
    /// Optional correlation id for cross-service audit traceability.
    /// Carried into log lines but not persisted.
    /// </summary>
    public string? CorrelationId { get; set; }
}

public sealed class NetworkTierBackfillMapping
{
    public string PlanId { get; set; } = string.Empty;
    public string TierName { get; set; } = string.Empty;
    public string NetworkId { get; set; } = string.Empty;
}

/// <summary>
/// Per-call summary returned to the operator. Counters mirror the
/// <c>cho.benefit_plan.network_tier.backfill.outcomes.total</c> meter.
/// </summary>
public sealed class NetworkTierBackfillResult
{
    public string BackfillRunId { get; set; } = Guid.NewGuid().ToString();
    public int MappingsSubmitted { get; set; }
    public int Patched { get; set; }
    public int Skipped { get; set; }
    public int NotFound { get; set; }
    public int Unresolved { get; set; }
    public int Failed { get; set; }
    public List<NetworkTierBackfillIssue> Issues { get; set; } = new();
}

public sealed class NetworkTierBackfillIssue
{
    public string PlanId { get; set; } = string.Empty;
    public string TierName { get; set; } = string.Empty;
    public string NetworkId { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string? Detail { get; set; }
}

public sealed class NetworkTierBackfillService : INetworkTierBackfillService
{
    private readonly IBenefitPlanRepository _repository;
    private readonly IOrganizationLookupClient _organizationLookup;
    private readonly IOptionsMonitor<NetworkTierBackfillOptions> _options;
    private readonly ILogger<NetworkTierBackfillService> _logger;

    public NetworkTierBackfillService(
        IBenefitPlanRepository repository,
        IOrganizationLookupClient organizationLookup,
        IOptionsMonitor<NetworkTierBackfillOptions> options,
        ILogger<NetworkTierBackfillService> logger)
    {
        _repository = repository;
        _organizationLookup = organizationLookup;
        _options = options;
        _logger = logger;
    }

    public async Task<NetworkTierBackfillResult> RunTenantAsync(
        string tenantId,
        NetworkTierBackfillRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(request);

        var result = new NetworkTierBackfillResult
        {
            MappingsSubmitted = request.Mappings.Count,
        };

        if (request.Mappings.Count == 0)
        {
            return result;
        }

        // Validate PlanId per mapping before grouping. Mappings with a
        // blank/whitespace PlanId are recorded as `failed` (visible in
        // both the per-tenant counter and the operator-facing result
        // summary) instead of being silently dropped — submitted-vs-
        // outcome totals stay in balance and the operator gets explicit
        // feedback.
        var validMappings = new List<NetworkTierBackfillMapping>();
        foreach (var mapping in request.Mappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.PlanId))
            {
                RecordOutcome(tenantId, "failed", mapping, result, detail: "planId is required.");
                result.Failed++;
                continue;
            }
            validMappings.Add(mapping);
        }

        // Group surviving mappings by planId so each plan is loaded and
        // patched exactly once even when the operator submits multiple
        // tier mappings against the same plan.
        var byPlan = validMappings.GroupBy(m => m.PlanId, StringComparer.Ordinal);

        foreach (var group in byPlan)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessPlanAsync(tenantId, group.Key, group.ToList(), result, request, ct);
        }

        _logger.LogInformation(
            "network-tier backfill complete tenant={Tenant} runId={RunId} submitted={Submitted} patched={Patched} skipped={Skipped} notFound={NotFound} unresolved={Unresolved} failed={Failed}",
            SanitizeForLog(tenantId), result.BackfillRunId,
            result.MappingsSubmitted, result.Patched, result.Skipped,
            result.NotFound, result.Unresolved, result.Failed);

        return result;
    }

    private async Task ProcessPlanAsync(
        string tenantId,
        string planId,
        List<NetworkTierBackfillMapping> mappings,
        NetworkTierBackfillResult result,
        NetworkTierBackfillRequest request,
        CancellationToken ct)
    {
        var plan = await _repository.GetLatestPublishedAsync(planId, tenantId, DateTime.UtcNow);
        if (plan is null)
        {
            foreach (var m in mappings)
            {
                RecordOutcome(tenantId, "not_found", m, result, detail: $"No Published version of plan {planId} found.");
                result.NotFound++;
            }
            return;
        }

        var tiers = plan.NetworkTiers.Select(CloneTier).ToList();
        // Buffer the mappings that resolved cleanly (organization
        // exists, tier exists, NetworkId is null today). Their `patched`
        // counter and result-summary increments are deferred until
        // UpdateNetworkTiersAsync returns true — a Prometheus counter
        // can't be decremented, so we never want to emit `patched` for
        // a mapping whose write later failed.
        var pendingPatched = new List<NetworkTierBackfillMapping>();

        foreach (var mapping in mappings)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(mapping.TierName) || string.IsNullOrWhiteSpace(mapping.NetworkId))
            {
                RecordOutcome(tenantId, "failed", mapping, result, detail: "tierName and networkId are required.");
                result.Failed++;
                continue;
            }

            var tier = tiers.FirstOrDefault(t => string.Equals(t.TierName, mapping.TierName, StringComparison.Ordinal));
            if (tier is null)
            {
                RecordOutcome(tenantId, "failed", mapping, result, detail: $"Tier '{mapping.TierName}' not found on plan.");
                result.Failed++;
                continue;
            }

            if (!string.IsNullOrEmpty(tier.NetworkId))
            {
                RecordOutcome(tenantId, "skipped", mapping, result, detail: "Tier already has NetworkId.");
                result.Skipped++;
                continue;
            }

            var organization = await _organizationLookup.GetOrganizationAsync(mapping.NetworkId, ct);
            if (organization is null)
            {
                RecordOutcome(tenantId, "unresolved", mapping, result, detail: "Organization not resolvable in provider-service.");
                result.Unresolved++;
                continue;
            }

            tier.NetworkId = mapping.NetworkId;
            pendingPatched.Add(mapping);
        }

        if (pendingPatched.Count == 0) return;

        bool writeOk;
        try
        {
            writeOk = await _repository.UpdateNetworkTiersAsync(tenantId, planId, tiers, ct);
        }
        catch (Exception ex)
        {
            // Repository patch threw — every pendingPatched mapping is
            // unwritten. Surface them as `failed` (counter + summary)
            // and continue with the next plan.
            _logger.LogError(ex,
                "network-tier backfill failed tenant={Tenant} planId={PlanId} runId={RunId}",
                SanitizeForLog(tenantId), SanitizeForLog(planId), result.BackfillRunId);
            foreach (var m in pendingPatched)
            {
                RecordOutcome(tenantId, "failed", m, result, detail: ex.GetType().Name);
                result.Failed++;
            }
            return;
        }

        if (!writeOk)
        {
            // Lost-write race — head row vanished between
            // GetLatestPublishedAsync and UpdateNetworkTiersAsync. None
            // of the pendingPatched mappings were written; record each
            // as `not_found`.
            _logger.LogWarning(
                "network-tier backfill: head row vanished mid-operation tenant={Tenant} planId={PlanId} runId={RunId}",
                SanitizeForLog(tenantId), SanitizeForLog(planId), result.BackfillRunId);
            foreach (var m in pendingPatched)
            {
                RecordOutcome(tenantId, "not_found", m, result, detail: "Head version disappeared during patch.");
                result.NotFound++;
            }
            return;
        }

        // Write succeeded → emit `patched` once per mapping.
        foreach (var m in pendingPatched)
        {
            ChoMetrics.NetworkTierBackfillOutcomes.Add(
                1,
                new KeyValuePair<string, object?>("cho.outcome", "patched"),
                new KeyValuePair<string, object?>("cho.tenant_id", tenantId));
            result.Patched++;
        }
    }

    private void RecordOutcome(
        string tenantId,
        string outcome,
        NetworkTierBackfillMapping mapping,
        NetworkTierBackfillResult result,
        string? detail)
    {
        ChoMetrics.NetworkTierBackfillOutcomes.Add(
            1,
            new KeyValuePair<string, object?>("cho.outcome", outcome),
            new KeyValuePair<string, object?>("cho.tenant_id", tenantId));

        result.Issues.Add(new NetworkTierBackfillIssue
        {
            PlanId = mapping.PlanId,
            TierName = mapping.TierName,
            NetworkId = mapping.NetworkId,
            Outcome = outcome,
            Detail = detail,
        });
    }

#pragma warning disable CS0618 // Cloning ProviderNpis preserves the legacy field during the migration window
    private static NetworkTier CloneTier(NetworkTier t) => new()
    {
        Id = t.Id,
        TierName = t.TierName,
        TierLevel = t.TierLevel,
        NetworkId = t.NetworkId,
        ProviderNpis = t.ProviderNpis.ToList(),
    };
#pragma warning restore CS0618

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
