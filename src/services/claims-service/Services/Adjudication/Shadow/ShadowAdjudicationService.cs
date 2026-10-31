using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Infrastructure.Logging;
using Microsoft.Extensions.Options;

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
}

/// <summary>Shadow runs are off for this tenant.</summary>
public sealed class ShadowAdjudicationDisabledException(string message) : Exception(message);

public sealed class ShadowAdjudicationService : IShadowAdjudicationService
{
    private readonly IClaimRepository _claims;
    private readonly IClaimImportTransactionRepository _transactions;
    private readonly IShadowAdjudicationPipeline _pipeline;
    private readonly IShadowAdjudicationRepository _store;
    private readonly ShadowAdjudicationOptions _options;
    private readonly ILogger<ShadowAdjudicationService> _logger;

    public ShadowAdjudicationService(
        IClaimRepository claims,
        IClaimImportTransactionRepository transactions,
        IShadowAdjudicationPipeline pipeline,
        IShadowAdjudicationRepository store,
        IOptions<ShadowAdjudicationOptions> options,
        ILogger<ShadowAdjudicationService> logger)
    {
        _claims = claims;
        _transactions = transactions;
        _pipeline = pipeline;
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ShadowAdjudicationRunResponse> RunAsync(
        string tenantId, ShadowAdjudicationRunRequest request, string? actorId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentNullException.ThrowIfNull(request);
        if (!_options.IsEnabledFor(tenantId))
            throw new ShadowAdjudicationDisabledException(
                $"Shadow adjudication is not enabled for this tenant ({ShadowAdjudicationOptions.SectionName}).");

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
                result = ShadowDiscrepancyComparer.NotComparable(core, tenantId);
            }
            else
            {
                try
                {
                    var context = await _pipeline
                        .EvaluateShadowAsync(tenantId, DetachedCopy(core), actorId, $"shadow:{run.Id}", ct)
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

        Summarize(run, results);
        run.CompletedAtUtc = DateTime.UtcNow;
        await _store.SaveRunAsync(run, results, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Shadow adjudication run {RunId}: {Compared} compared, {Skipped} skipped, {Failed} failed, match rate {MatchRate}",
            run.Id, run.ClaimsCompared, run.Skipped.Count, run.Failed.Count, run.MatchRate);

        return new ShadowAdjudicationRunResponse { Run = run, Results = results };
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
