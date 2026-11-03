using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Infrastructure.Logging;

namespace ClaimsService.Services.Adjudication.Shadow;

/// <summary>
/// Shadow adjudication: re-adjudicates claims another core (QNXT, Facets, ...)
/// already adjudicated and the CPCDS importer loaded, and records where CHO's
/// answer differs from the core's. Read-only for everything a member, provider
/// or FHIR API sees. See <c>docs/integration/SHADOW-ADJUDICATION.md</c>.
/// </summary>
public interface IShadowAdjudicationService
{
    Task<ShadowAdjudicationRunResponse> RunAsync(
        string tenantId, ShadowAdjudicationRunRequest request, string? actorId, CancellationToken ct);

    /// <summary>
    /// Queues a background run over every claim of an import batch (up to
    /// <see cref="ShadowAdjudicationOptions.MaxClaimsPerBackgroundRun"/>) and returns it at once.
    /// </summary>
    Task<ShadowAdjudicationRun> QueueAsync(
        string tenantId, ShadowAdjudicationRunRequest request, string? actorId, CancellationToken ct);

    /// <summary>
    /// Adjudicates a background run the worker took, page by page, saving
    /// results and progress after each page. Resumes where a stopped replica left off.
    /// </summary>
    Task ProcessBackgroundRunAsync(ShadowAdjudicationRun run, CancellationToken ct);
}

/// <summary>Shadow runs are off for this tenant.</summary>
public sealed class ShadowAdjudicationDisabledException(string message) : Exception(message);

public sealed class ShadowAdjudicationService : IShadowAdjudicationService
{
    private readonly IClaimRepository _claims;
    private readonly IClaimImportTransactionRepository _transactions;
    private readonly IShadowAdjudicationPipeline _pipeline;
    private readonly IShadowAdjudicationRepository _store;
    private readonly ShadowAdjudicationTenants _tenants;
    private readonly ShadowAdjudicationOptions _options;
    private readonly ILogger<ShadowAdjudicationService> _logger;

    public ShadowAdjudicationService(
        IClaimRepository claims,
        IClaimImportTransactionRepository transactions,
        IShadowAdjudicationPipeline pipeline,
        IShadowAdjudicationRepository store,
        ShadowAdjudicationTenants tenants,
        ILogger<ShadowAdjudicationService> logger)
    {
        _claims = claims;
        _transactions = transactions;
        _pipeline = pipeline;
        _store = store;
        _tenants = tenants;
        _options = tenants.Options;
        _logger = logger;
    }

    private async Task EnsureEnabledAsync(string tenantId, CancellationToken ct)
    {
        if (!await _tenants.IsEnabledForAsync(tenantId, ct).ConfigureAwait(false))
            throw new ShadowAdjudicationDisabledException(_options.Enabled
                ? "Shadow adjudication is off for this tenant. Turn it on in the portal (Shadow adjudication) or with PUT /api/v1/claims/shadow-adjudication/settings."
                : $"Shadow adjudication is off in this deployment ({ShadowAdjudicationOptions.SectionName}:Enabled).");
    }

    public async Task<ShadowAdjudicationRun> QueueAsync(
        string tenantId, ShadowAdjudicationRunRequest request, string? actorId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ImportBatchId))
            throw new ArgumentException("A background run needs an import batch (importBatchId).", nameof(request));
        await EnsureEnabledAsync(tenantId, ct).ConfigureAwait(false);

        var max = Math.Max(1, _options.MaxClaimsPerBackgroundRun);
        var run = new ShadowAdjudicationRun
        {
            TenantId = tenantId,
            RequestedBy = actorId,
            ImportBatchId = request.ImportBatchId.Trim(),
            Background = true,
            Status = ShadowRunStatus.Queued,
            ClaimsRequested = Math.Clamp(request.Limit ?? max, 1, max),
            Tolerance = _options.Tolerance,
            StartedAtUtc = DateTime.UtcNow,
        };
        await _store.SaveRunStateAsync(run, ct).ConfigureAwait(false);
        _logger.LogInformation("Shadow adjudication run {RunId} queued for import batch {ImportBatchId}",
            run.Id, LogSanitizer.Sanitize(run.ImportBatchId));
        return run;
    }

    public async Task ProcessBackgroundRunAsync(ShadowAdjudicationRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        try
        {
            await EnsureEnabledAsync(run.TenantId, ct).ConfigureAwait(false);
        }
        catch (ShadowAdjudicationDisabledException ex)
        {
            await FinishAsync(run, ShadowRunStatus.Failed, ex.Message, ct).ConfigureAwait(false);
            return;
        }

        // ClaimsRequested holds the cap when queued; it becomes the batch's claim count.
        var claimIds = await SelectClaimIdsAsync(
            run.TenantId, new ShadowAdjudicationRunRequest(), run.ImportBatchId,
            Math.Max(1, run.ClaimsRequested), ct).ConfigureAwait(false);
        run.ClaimsRequested = claimIds.Count;

        // Resuming: leave out claims that already have a result, or were skipped or failed.
        var handled = new HashSet<string>(
            await _store.ListResultClaimIdsAsync(run.TenantId, run.Id, ct).ConfigureAwait(false), StringComparer.Ordinal);
        foreach (var entry in run.Skipped.Concat(run.Failed))
            handled.Add(ClaimIdOf(entry));
        var remaining = claimIds.Where(id => !handled.Contains(id)).ToList();
        run.ClaimsProcessed = claimIds.Count - remaining.Count;

        var pageSize = Math.Max(1, _options.MaxClaimsPerRun);
        foreach (var page in remaining.Chunk(pageSize))
        {
            ct.ThrowIfCancellationRequested();
            var results = await CompareAsync(run, page, run.RequestedBy, ct).ConfigureAwait(false);
            await _store.AppendResultsAsync(results, ct).ConfigureAwait(false);
            run.ClaimsProcessed += page.Length;
            run.LeaseUntilUtc = DateTime.UtcNow + _options.LeaseDuration;
            await _store.SaveRunStateAsync(run, ct).ConfigureAwait(false);
        }

        var all = await _store.ListResultsAsync(run.TenantId, run.Id, kind: null,
            Math.Max(1, _options.MaxClaimsPerBackgroundRun), ct).ConfigureAwait(false);
        Summarize(run, all);
        await FinishAsync(run, ShadowRunStatus.Completed, null, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Shadow adjudication background run {RunId}: {Compared} compared, {Skipped} skipped, {Failed} failed, match rate {MatchRate}",
            run.Id, run.ClaimsCompared, run.Skipped.Count, run.Failed.Count, run.MatchRate);
    }

    private Task FinishAsync(ShadowAdjudicationRun run, string status, string? error, CancellationToken ct)
    {
        run.Status = status;
        run.Error = error;
        run.LeaseUntilUtc = null;
        run.CompletedAtUtc = DateTime.UtcNow;
        return _store.SaveRunStateAsync(run, ct);
    }

    /// <summary>"claimId: reason" entries in Skipped and Failed.</summary>
    private static string ClaimIdOf(string entry)
    {
        var colon = entry.IndexOf(':');
        return colon > 0 ? entry[..colon] : entry;
    }

    public async Task<ShadowAdjudicationRunResponse> RunAsync(
        string tenantId, ShadowAdjudicationRunRequest request, string? actorId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentNullException.ThrowIfNull(request);
        await EnsureEnabledAsync(tenantId, ct).ConfigureAwait(false);

        var max = Math.Max(1, _options.MaxClaimsPerRun);
        var limit = Math.Clamp(request.Limit ?? max, 1, max);
        var run = new ShadowAdjudicationRun
        {
            TenantId = tenantId,
            RequestedBy = actorId,
            ImportBatchId = string.IsNullOrWhiteSpace(request.ImportBatchId) ? null : request.ImportBatchId.Trim(),
            Tolerance = _options.Tolerance,
            StartedAtUtc = DateTime.UtcNow,
        };

        var claimIds = await SelectClaimIdsAsync(tenantId, request, run.ImportBatchId, limit, ct).ConfigureAwait(false);
        run.ClaimsRequested = claimIds.Count;

        var results = await CompareAsync(run, claimIds, actorId, ct).ConfigureAwait(false);
        run.ClaimsProcessed = claimIds.Count;

        Summarize(run, results);
        run.CompletedAtUtc = DateTime.UtcNow;
        await _store.SaveRunAsync(run, results, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Shadow adjudication run {RunId}: {Compared} compared, {Skipped} skipped, {Failed} failed, match rate {MatchRate}",
            run.Id, run.ClaimsCompared, run.Skipped.Count, run.Failed.Count, run.MatchRate);

        return new ShadowAdjudicationRunResponse { Run = run, Results = results };
    }

    /// <summary>Compares each claim; adds skipped and failed claims to the run.</summary>
    private async Task<List<ShadowAdjudicationResult>> CompareAsync(
        ShadowAdjudicationRun run, IReadOnlyCollection<string> claimIds, string? actorId, CancellationToken ct)
    {
        var results = new List<ShadowAdjudicationResult>(claimIds.Count);
        foreach (var claimId in claimIds)
        {
            ct.ThrowIfCancellationRequested();
            var core = await _claims.GetLatestVersionAsync(claimId, DateTime.UtcNow).ConfigureAwait(false)
                       ?? await _claims.GetByIdAsync(claimId).ConfigureAwait(false);
            if (core is null)
            {
                run.Skipped.Add($"{claimId}: not found");
                continue;
            }
            if (core.ExternalSource is null)
            {
                // Only claims another core adjudicated have an answer to compare with.
                run.Skipped.Add($"{claimId}: not loaded from a core system");
                continue;
            }

            ShadowAdjudicationResult result;
            if (ShadowDiscrepancyComparer.CoreOutcome(core.Status) == ShadowOutcome.NotFinal)
            {
                result = ShadowDiscrepancyComparer.NotComparable(core, run.TenantId);
            }
            else
            {
                try
                {
                    var context = await _pipeline
                        .EvaluateShadowAsync(run.TenantId, DetachedCopy(core), actorId, $"shadow:{run.Id}", ct)
                        .ConfigureAwait(false);
                    result = ShadowDiscrepancyComparer.Compare(core, context, _options.Tolerance);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Shadow adjudication failed for claim {ClaimId}", LogSanitizer.Sanitize(claimId));
                    run.Failed.Add($"{claimId}: {ex.GetType().Name}");
                    continue;
                }
            }

            result.RunId = run.Id;
            results.Add(result);
        }
        return results;
    }

    private async Task<IReadOnlyList<string>> SelectClaimIdsAsync(
        string tenantId, ShadowAdjudicationRunRequest request, string? importBatchId, int limit, CancellationToken ct)
    {
        var ids = new List<string>();
        if (request.ClaimIds is { Count: > 0 })
            ids.AddRange(request.ClaimIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()));

        if (importBatchId is not null)
        {
            var transactions = await _transactions
                .ListByImportBatchAsync(tenantId, importBatchId, limit, ct)
                .ConfigureAwait(false);
            ids.AddRange(transactions
                .Where(t => !string.IsNullOrWhiteSpace(t.ClaimId))
                .Select(t => t.ClaimId!));
        }

        return ids.Distinct(StringComparer.Ordinal).Take(limit).ToList();
    }

    /// <summary>
    /// The claim as CHO would have received it: the same claim data, with the
    /// core's answer removed. A new object graph, so nothing the pipeline does
    /// to it reaches the stored claim.
    /// </summary>
    internal static AdapterClaim DetachedCopy(Claim core)
    {
        var copy = AdapterClaim.From(core);
        copy.AdjudicationResult = null;
        copy.PendDetails = null;
        copy.AiExamination = null;
        copy.Status = ClaimStatus.Submitted;
        copy.AdjudicatedDate = null;
        copy.PaidDate = null;
        foreach (var line in copy.ClaimLines)
            line.AdjudicationResult = null;
        return copy;
    }

    internal static void Summarize(ShadowAdjudicationRun run, IReadOnlyList<ShadowAdjudicationResult> results)
    {
        run.ClaimsCompared = results.Count;
        run.CountsByKind = ShadowDiscrepancyKind.All
            .ToDictionary(k => k, k => results.Count(r => r.Kind == k), StringComparer.Ordinal);

        var comparable = results.Where(r => r.Kind != ShadowDiscrepancyKind.NotComparable).ToList();
        var decided = comparable.Where(r => r.Kind != ShadowDiscrepancyKind.ChoUndecided).ToList();

        run.UndecidedRate = comparable.Count == 0
            ? null
            : Math.Round((decimal)(comparable.Count - decided.Count) / comparable.Count, 4);
        run.MatchRate = decided.Count == 0
            ? null
            : Math.Round((decimal)decided.Count(r => r.Kind == ShadowDiscrepancyKind.Match) / decided.Count, 4);
        run.NetPaidVariance = decided.Sum(r => r.PaidVariance);
        run.AbsolutePaidVariance = decided.Sum(r => r.AbsolutePaidVariance);
    }
}
