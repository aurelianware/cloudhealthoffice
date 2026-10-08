namespace CloudHealthOffice.ProviderVerificationEngine.DataSources.Exclusions;

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using CloudHealthOffice.ProviderVerificationEngine.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>Downloads one exclusion list's bulk file and loads it into the local store.</summary>
public interface IExclusionDatasetSync
{
    ExclusionScreeningSource Source { get; }

    Task<ExclusionDatasetSyncResult> SyncAsync(CancellationToken ct = default);
}

public sealed class ExclusionDatasetSyncResult
{
    public ExclusionScreeningSource Source { get; init; }
    public bool Succeeded { get; init; }

    /// <summary>Set when the run did not attempt a download (e.g. another replica holds the lease).</summary>
    public string? SkippedReason { get; init; }

    public int RecordCount { get; init; }
    public int RowsSkipped { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset? SyncedAt { get; init; }
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Shared download → parse → load pipeline. The file is downloaded to a temp
/// file first (so a slow store never holds the HTTP connection open), parsed
/// lazily, and loaded with <see cref="IExclusionRecordStore.ReplaceDatasetAsync"/>,
/// which keeps the previous dataset on any failure. A failed sync never
/// refreshes the sync timestamp, so the staleness window eventually reports
/// providers NOT screened rather than clear.
/// </summary>
public abstract class ExclusionDatasetSyncBase : IExclusionDatasetSync
{
    public const string HttpClientName = "ExclusionDatasetDownload";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IExclusionRecordStore _store;
    private readonly TimeProvider _time;
    private readonly string _leaseHolder = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    protected ExclusionDatasetSyncBase(
        IHttpClientFactory httpFactory,
        IExclusionRecordStore store,
        IOptions<ExclusionScreeningOptions> options,
        ILogger logger,
        TimeProvider? time)
    {
        _httpFactory = httpFactory;
        _store = store;
        Options = options.Value;
        Logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public abstract ExclusionScreeningSource Source { get; }

    protected ExclusionScreeningOptions Options { get; }
    protected ILogger Logger { get; }

    /// <summary>Download URL without credentials (safe to log/store).</summary>
    protected abstract string DisplayUrl { get; }

    /// <summary>Actual request URL (may carry an API key — never log it).</summary>
    protected abstract string? BuildRequestUrl(out string? notConfiguredReason);

    protected abstract int MinimumRecordCount { get; }

    protected abstract IEnumerable<ExclusionRecord> Parse(string downloadedFile, ExclusionParseStats stats);

    public async Task<ExclusionDatasetSyncResult> SyncAsync(CancellationToken ct = default)
    {
        var label = LocalExclusionListScreener.Label(Source);
        var started = _time.GetUtcNow();
        var sw = Stopwatch.StartNew();

        var url = BuildRequestUrl(out var notConfigured);
        if (url is null)
        {
            Logger.LogWarning("{Source} sync not run: {Reason}", label, notConfigured);
            return Fail(notConfigured ?? "not configured", sw);
        }

        if (!await _store.TryAcquireSyncLeaseAsync(Source, _leaseHolder, started, Options.SyncLeaseDuration, ct).ConfigureAwait(false))
        {
            return new ExclusionDatasetSyncResult
            {
                Source = Source, SkippedReason = "Another replica is syncing this dataset", Duration = sw.Elapsed
            };
        }

        var tempFile = Path.Combine(Path.GetTempPath(), $"cho-exclusions-{Source}-{Guid.NewGuid():N}.tmp");
        try
        {
            var downloadError = await DownloadAsync(url, tempFile, label, ct).ConfigureAwait(false);
            if (downloadError is not null)
            {
                await _store.RecordSyncFailureAsync(Source, downloadError, _time.GetUtcNow(), ct).ConfigureAwait(false);
                return Fail(downloadError, sw);
            }

            var stats = new ExclusionParseStats();
            var syncedAt = _time.GetUtcNow();
            var load = await _store.ReplaceDatasetAsync(
                Source,
                Parse(tempFile, stats),
                new ExclusionDatasetLoadPolicy
                {
                    SourceUrl = DisplayUrl,
                    SyncedAt = syncedAt,
                    MinimumRecordCount = MinimumRecordCount,
                    MinimumRetainedFraction = Options.MinimumRetainedFraction
                },
                ct).ConfigureAwait(false);

            if (!load.Succeeded)
            {
                Logger.LogError("{Source} sync rejected: {Error}", label, load.Error);
                await _store.RecordSyncFailureAsync(Source, load.Error ?? "load rejected", _time.GetUtcNow(), ct).ConfigureAwait(false);
                return Fail(load.Error ?? "load rejected", sw);
            }

            Logger.LogInformation(
                "{Source} sync complete: {Count} records loaded ({Skipped} rows skipped, previously {Previous}) in {Elapsed}. " +
                "Providers last screened before {SyncedAt:O} are due for exclusion re-screening.",
                label, load.RecordCount, stats.RowsSkipped, load.PreviousRecordCount, sw.Elapsed, syncedAt);

            return new ExclusionDatasetSyncResult
            {
                Source = Source,
                Succeeded = true,
                RecordCount = load.RecordCount,
                RowsSkipped = stats.RowsSkipped,
                SyncedAt = syncedAt,
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var error = ex is ExclusionFileFormatException or InvalidDataException
                ? ex.Message
                : $"{ex.GetType().Name} during sync";
            Logger.LogError(ex, "{Source} sync failed; previous dataset kept", label);
            await TryRecordFailureAsync(error).ConfigureAwait(false);
            return Fail(error, sw);
        }
        finally
        {
            TryDelete(tempFile);
            try
            {
                await _store.ReleaseSyncLeaseAsync(Source, _leaseHolder, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Releasing {Source} sync lease failed; it expires on its own", label);
            }
        }
    }

    private async Task<string?> DownloadAsync(string url, string tempFile, string label, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient(HttpClientName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Options.DownloadTimeout);

        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                Logger.LogWarning(
                    "{Source} download was refused (HTTP {StatusCode}). If an API key is configured it was rejected — " +
                    "SAM.gov keys expire every 90 days; rotate the key. Previous dataset kept.",
                    label, (int)response.StatusCode);
                return $"{label} download refused (HTTP {(int)response.StatusCode}) — API key rejected or expired";
            }
            if (!response.IsSuccessStatusCode)
            {
                Logger.LogWarning("{Source} download failed with HTTP {StatusCode}", label, (int)response.StatusCode);
                return $"{label} download failed (HTTP {(int)response.StatusCode})";
            }

            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var file = File.Create(tempFile);
            await source.CopyToAsync(file, timeout.Token).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Logger.LogWarning("{Source} download timed out after {Timeout}", label, Options.DownloadTimeout);
            return $"{label} download timed out";
        }
        catch (HttpRequestException ex)
        {
            // HttpRequestException messages do not include the request URL.
            Logger.LogWarning("{Source} download failed: {Error}", label, ex.Message);
            return $"{label} download failed ({ex.Message})";
        }
    }

    private async Task TryRecordFailureAsync(string error)
    {
        try
        {
            await _store.RecordSyncFailureAsync(Source, error, _time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Recording sync failure failed");
        }
    }

    private ExclusionDatasetSyncResult Fail(string error, Stopwatch sw) => new()
    {
        Source = Source, Succeeded = false, Error = error, Duration = sw.Elapsed
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Opens a downloaded file as text; a ZIP archive yields its CSV entry.</summary>
    protected static IEnumerable<ExclusionRecord> ParseText(
        string file, Func<TextReader, ExclusionParseStats, IEnumerable<ExclusionRecord>> parse, ExclusionParseStats stats)
    {
        using var stream = File.OpenRead(file);
        var isZip = stream.Length >= 4 && stream.ReadByte() == 'P' && stream.ReadByte() == 'K';
        stream.Position = 0;

        if (!isZip)
        {
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            foreach (var record in parse(reader, stats))
                yield return record;
            yield break;
        }

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = zip.Entries
            .Where(e => e.Length > 0)
            .OrderByDescending(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(e => e.Length)
            .FirstOrDefault()
            ?? throw new ExclusionFileFormatException("Downloaded ZIP contains no data file");

        using var entryReader = new StreamReader(entry.Open(), detectEncodingFromByteOrderMarks: true);
        foreach (var record in parse(entryReader, stats))
            yield return record;
    }
}

/// <summary>OIG LEIE: monthly full database <c>UPDATED.csv</c> (no credentials).</summary>
public sealed class LeieDatasetSync : ExclusionDatasetSyncBase
{
    public LeieDatasetSync(
        IHttpClientFactory httpFactory,
        IExclusionRecordStore store,
        IOptions<ExclusionScreeningOptions> options,
        ILogger<LeieDatasetSync> logger,
        TimeProvider? time = null)
        : base(httpFactory, store, options, logger, time)
    {
    }

    public override ExclusionScreeningSource Source => ExclusionScreeningSource.OigLeie;

    protected override string DisplayUrl => Options.Leie.DownloadUrl;

    protected override int MinimumRecordCount => Options.Leie.MinimumRecordCount;

    protected override string? BuildRequestUrl(out string? notConfiguredReason)
    {
        notConfiguredReason = string.IsNullOrWhiteSpace(Options.Leie.DownloadUrl) ? "LEIE DownloadUrl is empty" : null;
        return notConfiguredReason is null ? Options.Leie.DownloadUrl : null;
    }

    protected override IEnumerable<ExclusionRecord> Parse(string downloadedFile, ExclusionParseStats stats) =>
        ParseText(downloadedFile, LeieCsvParser.Parse, stats);
}

/// <summary>
/// SAM.gov public exclusions extract via the Extracts download API (zipped
/// CSV). One request per sync regardless of network size; the API key is
/// appended to the URL and never logged.
/// </summary>
public sealed class SamExtractDatasetSync : ExclusionDatasetSyncBase
{
    public SamExtractDatasetSync(
        IHttpClientFactory httpFactory,
        IExclusionRecordStore store,
        IOptions<ExclusionScreeningOptions> options,
        ILogger<SamExtractDatasetSync> logger,
        TimeProvider? time = null)
        : base(httpFactory, store, options, logger, time)
    {
    }

    public override ExclusionScreeningSource Source => ExclusionScreeningSource.SamGov;

    protected override string DisplayUrl => Options.Sam.ExtractDownloadUrl;

    protected override int MinimumRecordCount => Options.Sam.MinimumRecordCount;

    protected override string? BuildRequestUrl(out string? notConfiguredReason)
    {
        if (string.IsNullOrWhiteSpace(Options.Sam.ApiKey))
        {
            notConfiguredReason = "SAM.gov API key not configured";
            return null;
        }
        if (string.IsNullOrWhiteSpace(Options.Sam.ExtractDownloadUrl))
        {
            notConfiguredReason = "SAM ExtractDownloadUrl is empty";
            return null;
        }

        notConfiguredReason = null;
        var separator = Options.Sam.ExtractDownloadUrl.Contains('?') ? '&' : '?';
        return $"{Options.Sam.ExtractDownloadUrl}{separator}api_key={Uri.EscapeDataString(Options.Sam.ApiKey)}";
    }

    protected override IEnumerable<ExclusionRecord> Parse(string downloadedFile, ExclusionParseStats stats) =>
        ParseText(downloadedFile, SamExtractCsvParser.Parse, stats);
}
