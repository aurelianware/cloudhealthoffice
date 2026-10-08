namespace CloudHealthOffice.ProviderVerificationEngine.DataSources.Exclusions;

using CloudHealthOffice.ProviderVerificationEngine.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>One exclusion list the composite adapter screens against.</summary>
public interface IExclusionSource
{
    ExclusionScreeningSource Source { get; }

    /// <summary>
    /// Screen one provider. Implementations must not throw for source
    /// unavailability: they return a result with <c>WasScreened = false</c>
    /// and a note instead.
    /// </summary>
    Task<ExclusionSourceOutcome> ScreenAsync(ProviderScreeningRequest request, CancellationToken ct);
}

public sealed class ExclusionSourceOutcome
{
    public required ExclusionSourceScreening Status { get; init; }
    public bool IsExcluded { get; init; }
    public List<ExclusionMatch> Matches { get; init; } = [];
}

/// <summary>
/// Screens against a locally synced copy of an exclusion list (OIG LEIE
/// UPDATED.csv, SAM.gov public extract). Reports <c>WasScreened = true</c>
/// only when the dataset has been synced within
/// <see cref="ExclusionScreeningOptions.StalenessWindow"/>. Against a stale
/// copy it still looks for hits — but reports NOT screened and downgrades
/// any hit to a possible match (the exclusion may since have ended), so stale
/// data never reads as clear and never auto-denies.
/// </summary>
public sealed class LocalExclusionListScreener : IExclusionSource
{
    public const string ModeName = "LocalDataset";

    private readonly IExclusionRecordStore _store;
    private readonly ExclusionScreeningOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public LocalExclusionListScreener(
        ExclusionScreeningSource source,
        IExclusionRecordStore store,
        IOptions<ExclusionScreeningOptions> options,
        ILogger<LocalExclusionListScreener> logger,
        TimeProvider? time = null)
    {
        Source = source;
        _store = store;
        _options = options.Value;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public ExclusionScreeningSource Source { get; }

    public async Task<ExclusionSourceOutcome> ScreenAsync(ProviderScreeningRequest request, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var status = new ExclusionSourceScreening { Source = Source, Mode = ModeName };

        try
        {
            var sync = await _store.GetSyncStatusAsync(Source, ct).ConfigureAwait(false);
            if (sync?.LastSuccessfulSyncAt is not { } syncedAt)
            {
                status.Note = $"{Label(Source)} dataset has never been synced";
                return new ExclusionSourceOutcome { Status = status };
            }

            status.DataAsOf = syncedAt;
            var fresh = now - syncedAt <= _options.StalenessWindow;

            var candidates = new List<ExclusionRecord>();
            var npi = ExclusionNameNormalizer.NormalizeNpi(request.Npi);
            if (npi is not null)
                candidates.AddRange(await _store.FindByNpiAsync(Source, npi, ct).ConfigureAwait(false));

            var last = ExclusionNameNormalizer.NormalizePersonName(request.LastName, stripSuffixes: true);
            if (last.Length > 0)
                candidates.AddRange(await _store.FindByLastNameAsync(Source, last, ct).ConfigureAwait(false));

            var org = ExclusionNameNormalizer.NormalizeBusinessName(request.OrganizationName);
            if (org.Length > 0)
                candidates.AddRange(await _store.FindByBusinessNameAsync(Source, org, ct).ConfigureAwait(false));

            var outcome = ExclusionMatcher.Match(request, candidates, now.UtcDateTime);

            if (!fresh)
            {
                status.Note = $"{Label(Source)} dataset is stale (last synced {syncedAt:yyyy-MM-dd}, " +
                              $"older than {_options.StalenessWindow.TotalDays:0} days)";
                foreach (var m in outcome.Matches.Where(m => m.MatchConfidence >= ExclusionMatcher.Definitive))
                {
                    m.MatchConfidence = 0.9f;
                    m.MatchNote += " (stale dataset — confirm current status)";
                }
                return new ExclusionSourceOutcome { Status = status, IsExcluded = false, Matches = outcome.Matches };
            }

            status.WasScreened = true;
            return new ExclusionSourceOutcome { Status = status, IsExcluded = outcome.IsExcluded, Matches = outcome.Matches };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "{Source} local exclusion store unavailable; reporting not screened", Label(Source));
            status.WasScreened = false;
            status.Note = $"{Label(Source)} local dataset unavailable ({ex.GetType().Name})";
            return new ExclusionSourceOutcome { Status = status };
        }
    }

    internal static string Label(ExclusionScreeningSource source) => source switch
    {
        ExclusionScreeningSource.OigLeie => "OIG LEIE",
        ExclusionScreeningSource.SamGov => "SAM.gov",
        _ => source.ToString()
    };
}
