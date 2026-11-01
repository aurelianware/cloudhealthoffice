using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Logging;
using Microsoft.Extensions.Options;

namespace ClaimsService.Extracts;

/// <summary>
/// The claims warehouse extract (portal-reporting.md §6). Off unless
/// <see cref="Enabled"/> for the scheduled run; each tenant also turns its
/// own extract on, naming its data-use agreement.
/// </summary>
public sealed class ClaimExtractOptions
{
    public const string SectionName = "ClaimExtract";

    /// <summary>Run the nightly extract for every tenant that turned it on.</summary>
    public bool Enabled { get; set; }

    /// <summary>The UTC time of day after which the day's extract runs.</summary>
    public TimeSpan RunAtUtc { get; set; } = TimeSpan.FromHours(6);

    /// <summary>How often the worker checks whether a tenant's extract is due.</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>A failed run is retried no sooner than this.</summary>
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// A run delivers changes up to this long before it starts, so a write
    /// stamped just before the run but committed just after is not skipped.
    /// </summary>
    public TimeSpan SettleDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a run holds the tenant. A run still going after this can be overlapped by another.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromHours(4);

    public int PageSize { get; set; } = 1000;
}

/// <summary>Why a run did not start.</summary>
public enum ClaimExtractRefusal
{
    None,
    NotEnabled,
    AlreadyRunning,
}

public sealed record ClaimExtractOutcome(ClaimExtractRun? Run, ClaimExtractRefusal Refusal);

/// <summary>
/// Delivers one tenant's claims extract: every claim version (drafts aside)
/// that changed since the last delivery, as <c>claims.csv</c> and
/// <c>claim-lines.csv</c>, then <c>manifest.json</c> with each file's row
/// count, size and SHA-256. The manifest is written last, so a plan's loader
/// that waits for it never reads a run still being written. The watermark
/// moves only after all three files are written; a failed run is simply
/// delivered again, whole, by the next one.
/// </summary>
public sealed class ClaimExtractRunner
{
    public const string Dataset = "claims";
    public const string ScheduleActor = "schedule";

    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IClaimExtractStore _store;
    private readonly IClaimExtractSource _source;
    private readonly IExtractDestination _destination;
    private readonly ClaimExtractOptions _options;
    private readonly ILogger<ClaimExtractRunner> _logger;
    private readonly TimeProvider _clock;

    public ClaimExtractRunner(
        IClaimExtractStore store,
        IClaimExtractSource source,
        IExtractDestination destination,
        IOptions<ClaimExtractOptions> options,
        ILogger<ClaimExtractRunner> logger,
        TimeProvider? clock = null)
    {
        _store = store;
        _source = source;
        _destination = destination;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <param name="full">Deliver every claim version, not just what changed since the last run.</param>
    public async Task<ClaimExtractOutcome> RunAsync(string tenantId, string requestedBy, bool full, CancellationToken ct)
    {
        var settings = await _store.GetSettingsAsync(tenantId, ct);
        if (settings is not { Enabled: true } || string.IsNullOrWhiteSpace(settings.DataUseAgreement))
            return new(null, ClaimExtractRefusal.NotEnabled);

        var startedAt = _clock.GetUtcNow().UtcDateTime;
        var owner = Guid.NewGuid().ToString("N");
        var leased = await _store.TryLeaseAsync(tenantId, owner, startedAt, startedAt + _options.LeaseDuration, ct);
        if (leased is null)
            return new(null, ClaimExtractRefusal.AlreadyRunning);

        var to = startedAt - _options.SettleDelay;
        var from = full ? null : leased.Watermark;
        var run = new ClaimExtractRun
        {
            Id = $"{to:yyyyMMdd'T'HHmmss'Z'}-{owner[..8]}",
            TenantId = tenantId,
            RequestedBy = requestedBy,
            Full = full || from is null,
            WindowFrom = from,
            WindowTo = to,
            StartedAt = startedAt,
            DataUseAgreement = leased.DataUseAgreement,
        };

        var work = Directory.CreateTempSubdirectory("cho-claim-extract-");
        try
        {
            if (from is { } f && f >= to)
                from = run.WindowFrom = to; // Nothing new can be in the window; deliver an empty run.

            var headersPath = Path.Combine(work.FullName, ClaimExtractColumns.HeadersFile);
            var linesPath = Path.Combine(work.FullName, ClaimExtractColumns.LinesFile);
            (run.ClaimVersions, run.Lines) = await WriteFilesAsync(tenantId, from, to, headersPath, linesPath, ct);

            var headers = await DeliverAsync(tenantId, run.Id, ClaimExtractColumns.HeadersFile, headersPath, run.ClaimVersions, ct);
            var lines = await DeliverAsync(tenantId, run.Id, ClaimExtractColumns.LinesFile, linesPath, run.Lines, ct);
            run.Files = [headers, lines];

            var manifestPath = Path.Combine(work.FullName, ClaimExtractColumns.ManifestFile);
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(Manifest(run), ManifestJson), Encoding.UTF8, ct);
            var manifestAt = await _destination.WriteAsync(tenantId, Dataset, run.Id, ClaimExtractColumns.ManifestFile, manifestPath, ct);
            run.Files.Add(new ClaimExtractFile(ClaimExtractColumns.ManifestFile, 0, new FileInfo(manifestPath).Length,
                await Sha256Async(manifestPath, ct), manifestAt));

            run.Status = ClaimExtractRunStatus.Succeeded;
            run.CompletedAt = _clock.GetUtcNow().UtcDateTime;
            await _store.SaveRunAsync(run, ct);
            await _store.ReleaseAsync(tenantId, owner, to, run.CompletedAt, CancellationToken.None);
            _logger.LogInformation("Claims extract {RunId} for tenant {TenantId}: {Versions} claim versions, {Lines} lines",
                run.Id, LogSanitizer.Sanitize(tenantId), run.ClaimVersions, run.Lines);
        }
        catch (Exception ex)
        {
            run.Status = ClaimExtractRunStatus.Failed;
            run.CompletedAt = _clock.GetUtcNow().UtcDateTime;
            // The type and message only: a claim's content never goes into the run record.
            run.Error = $"{ex.GetType().Name}: {ex.Message}";
            await _store.SaveRunAsync(run, CancellationToken.None);
            await _store.ReleaseAsync(tenantId, owner, null, null, CancellationToken.None);
            _logger.LogError(ex, "Claims extract {RunId} failed for tenant {TenantId}", run.Id, LogSanitizer.Sanitize(tenantId));
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
        }
        finally
        {
            try { work.Delete(recursive: true); }
            catch (IOException) { /* temp files; the OS clears them */ }
        }

        return new(run, ClaimExtractRefusal.None);
    }

    private async Task<(long Versions, long Lines)> WriteFilesAsync(
        string tenantId, DateTime? from, DateTime to, string headersPath, string linesPath, CancellationToken ct)
    {
        long versions = 0, lines = 0;
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        await using var headersOut = new StreamWriter(headersPath, false, utf8) { NewLine = "\r\n" };
        await using var linesOut = new StreamWriter(linesPath, false, utf8) { NewLine = "\r\n" };
        await headersOut.WriteLineAsync(ClaimExtractRows.Record(ClaimExtractColumns.Headers));
        await linesOut.WriteLineAsync(ClaimExtractRows.Record(ClaimExtractColumns.Lines));

        (DateTime, string)? after = null;
        while (true)
        {
            var page = await _source.ReadAsync(tenantId, from, to, after, _options.PageSize, ct);
            foreach (var claim in page)
            {
                await headersOut.WriteLineAsync(ClaimExtractRows.Record(ClaimExtractRows.Header(claim)));
                versions++;
                foreach (var line in ClaimExtractRows.Lines(claim))
                {
                    await linesOut.WriteLineAsync(ClaimExtractRows.Record(line));
                    lines++;
                }
            }
            if (page.Count < _options.PageSize) break;
            var last = page[^1];
            after = (last.LastUpdatedDate, last.Id);
        }
        return (versions, lines);
    }

    private async Task<ClaimExtractFile> DeliverAsync(string tenantId, string runId, string name, string path, long rows, CancellationToken ct)
    {
        var location = await _destination.WriteAsync(tenantId, Dataset, runId, name, path, ct);
        return new ClaimExtractFile(name, rows, new FileInfo(path).Length, await Sha256Async(path, ct), location);
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    private static object Manifest(ClaimExtractRun run) => new
    {
        dataset = Dataset,
        schemaVersion = ClaimExtractColumns.SchemaVersion,
        tenantId = run.TenantId,
        runId = run.Id,
        full = run.Full,
        windowFrom = run.WindowFrom,
        windowTo = run.WindowTo,
        generatedAt = run.StartedAt,
        dataUseAgreement = run.DataUseAgreement,
        files = run.Files.Select(f => new
        {
            name = f.Name,
            rows = f.Rows,
            bytes = f.Bytes,
            sha256 = f.Sha256,
            columns = f.Name == ClaimExtractColumns.HeadersFile ? ClaimExtractColumns.Headers : ClaimExtractColumns.Lines,
        }),
        notes = new[]
        {
            "Rows are claim versions whose LastUpdatedDate is after windowFrom (or from the beginning when full) and at or before windowTo. Drafts are not included.",
            "A version appears again in a later run whenever it changes. Load claims.csv by ClaimId, keeping the row with the latest LastUpdatedDate, and replace that version's lines with the ones in claim-lines.csv.",
            "A claim's versions share ClaimVersionId. The current version is the one with an empty SupersededAt.",
            "Instants are UTC ISO 8601; service dates are calendar dates. Lists inside a field are separated by '|' (codes) or ':' (modifiers, pointers).",
        },
    };
}
