namespace CloudHealthOffice.ProviderVerificationEngine.DataSources.Exclusions;

using System.Diagnostics;
using CloudHealthOffice.ProviderVerificationEngine.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Screens a provider against every enabled exclusion source (OIG LEIE,
/// SAM.gov) and combines the outcomes:
/// <list type="bullet">
///   <item><c>WasScreened</c> is true only when there is at least one source
///   and every enabled source screened with current data.</item>
///   <item><c>IsExcluded</c> is true when any source reports a definitive
///   exclusion — even if another source could not be screened.</item>
///   <item>Matches from all sources are combined, strongest first.</item>
///   <item><c>SourceResults</c> records each source's outcome and reason.</item>
/// </list>
/// A source that throws is recorded as not screened; this adapter never
/// throws for source unavailability.
/// </summary>
public sealed class CompositeExclusionScreeningAdapter : IExclusionScreeningAdapter
{
    private readonly IReadOnlyList<IExclusionSource> _sources;
    private readonly IReadOnlyList<IExclusionDatasetSync> _syncs;
    private readonly ILogger<CompositeExclusionScreeningAdapter> _logger;

    public CompositeExclusionScreeningAdapter(
        IEnumerable<IExclusionSource> sources,
        IEnumerable<IExclusionDatasetSync> syncs,
        ILogger<CompositeExclusionScreeningAdapter> logger)
    {
        _sources = sources.ToList();
        _syncs = syncs.ToList();
        _logger = logger;
    }

    public Task<ExclusionScreeningResult> ScreenProviderAsync(
        string npi,
        string? firstName = null,
        string? lastName = null,
        DateTimeOffset? dateOfBirth = null,
        CancellationToken ct = default) =>
        ScreenAsync(new ProviderScreeningRequest
        {
            Npi = npi,
            FirstName = firstName,
            LastName = lastName,
            DateOfBirth = dateOfBirth
        }, ct);

    public async Task<ExclusionScreeningResult> ScreenAsync(ProviderScreeningRequest request, CancellationToken ct = default)
    {
        var outcomes = await Task.WhenAll(_sources.Select(s => ScreenOneAsync(s, request, ct))).ConfigureAwait(false);

        var matches = outcomes.SelectMany(o => o.Matches)
            .OrderByDescending(m => m.MatchConfidence)
            .ToList();
        var excludedBy = outcomes.FirstOrDefault(o => o.IsExcluded);

        return new ExclusionScreeningResult
        {
            WasScreened = outcomes.Length > 0 && outcomes.All(o => o.Status.WasScreened),
            IsExcluded = excludedBy is not null,
            Matches = matches,
            ScreenedAt = DateTimeOffset.UtcNow,
            Source = excludedBy?.Status.Source
                     ?? matches.FirstOrDefault()?.Source
                     ?? _sources.FirstOrDefault()?.Source
                     ?? ExclusionScreeningSource.OigLeie,
            SourceResults = outcomes.Select(o => o.Status).ToList()
        };
    }

    public async Task<List<ExclusionScreeningResult>> BatchScreenAsync(
        IEnumerable<ProviderScreeningRequest> providers,
        CancellationToken ct = default)
    {
        var results = new List<ExclusionScreeningResult>();
        foreach (var provider in providers)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await ScreenAsync(provider, ct).ConfigureAwait(false));
        }
        return results;
    }

    public async Task<BulkSyncResult> SyncExclusionListsAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new BulkSyncResult { Source = string.Join("+", _syncs.Select(s => LocalExclusionListScreener.Label(s.Source))) };
        foreach (var sync in _syncs)
        {
            var run = await sync.SyncAsync(ct).ConfigureAwait(false);
            result.RecordsProcessed += run.RecordCount;
            result.RecordsInserted += run.RecordCount;
            result.RecordsSkipped += run.RowsSkipped;
            if (!run.Succeeded && run.SkippedReason is null)
                result.Errors++;
        }
        result.Duration = sw.Elapsed;
        result.CompletedAt = DateTimeOffset.UtcNow;
        return result;
    }

    private async Task<ExclusionSourceOutcome> ScreenOneAsync(IExclusionSource source, ProviderScreeningRequest request, CancellationToken ct)
    {
        try
        {
            return await source.ScreenAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var label = LocalExclusionListScreener.Label(source.Source);
            _logger.LogWarning(ex, "{Source} exclusion screening failed; reporting not screened", label);
            return new ExclusionSourceOutcome
            {
                Status = new ExclusionSourceScreening
                {
                    Source = source.Source,
                    WasScreened = false,
                    Note = $"{label} screening failed ({ex.GetType().Name})"
                }
            };
        }
    }
}
