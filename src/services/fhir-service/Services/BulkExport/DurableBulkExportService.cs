using System.Globalization;
using System.Text.RegularExpressions;
using FhirService.Models;
using Microsoft.Extensions.Options;

namespace FhirService.Services.BulkExport;

/// <summary>
/// Bulk Data <c>$export</c> for Provider Access (G-34): a provider's export of
/// its own attributed members, run by <see cref="BulkExportWorker"/> and kept
/// in <see cref="IBulkExportJobStore"/>.
///
/// <para>
/// The Group is the provider's own panel, so its id is the caller's own
/// provider id. A provider exporting any other Group is answered as if that
/// Group did not exist. A system-level <c>$export</c> (every member in the
/// tenant) is still refused as not implemented: it has no approved caller.
/// </para>
/// </summary>
public sealed partial class DurableBulkExportService : IBulkExportService
{
    private static readonly HashSet<string> NdjsonFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/fhir+ndjson", "application/ndjson", "ndjson",
    };

    private readonly IBulkExportJobStore _jobs;
    private readonly IBulkExportFileStore _files;
    private readonly IOptions<BulkExportOptions> _options;
    private readonly TimeProvider _time;

    public DurableBulkExportService(
        IBulkExportJobStore jobs, IBulkExportFileStore files, IOptions<BulkExportOptions> options, TimeProvider time)
    {
        _jobs = jobs;
        _files = files;
        _options = options;
        _time = time;
    }

    public async Task<BulkExportJob> InitiateExportAsync(
        BulkExportRequest request, string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.GroupId))
            throw new BulkExportNotImplementedException();

        if (string.IsNullOrWhiteSpace(request.RequestedBy)
            || !string.Equals(request.GroupId, request.RequestedBy, StringComparison.Ordinal))
            throw new BulkExportGroupNotFoundException();

        if (request.OutputFormat is { } format && !NdjsonFormats.Contains(format))
            throw new BulkExportRequestException("_outputFormat must be application/fhir+ndjson.");

        var record = new BulkExportJobRecord
        {
            TenantId = tenantId,
            JobId = Guid.NewGuid().ToString("N"),
            RequestedBy = request.RequestedBy,
            GroupId = request.GroupId,
            Types = ParseTypes(request.Type),
            Since = ParseSince(request.Since),
            RequestUrl = request.RequestUrl ?? string.Empty,
            CreatedAt = _time.GetUtcNow(),
        };
        await _jobs.CreateAsync(record, ct);
        return record.ToJob();
    }

    /// <summary>
    /// The job, while it can still be acted on. A job whose files were deleted,
    /// or a completed one past its <see cref="BulkExportOptions.FileLifetime"/>,
    /// is gone as far as a client is concerned.
    /// </summary>
    public async Task<BulkExportJob?> GetJobStatusAsync(string jobId, string tenantId, CancellationToken ct = default)
    {
        var record = await _jobs.GetAsync(tenantId, jobId, ct);
        return record is null || IsGone(record) ? null : record.ToJob();
    }

    public async Task<bool> CancelJobAsync(string jobId, string tenantId, CancellationToken ct = default)
    {
        var record = await _jobs.GetAsync(tenantId, jobId, ct);
        if (record is null || IsGone(record))
            return false;
        return await _jobs.CancelAsync(tenantId, jobId, _time.GetUtcNow(), ct);
    }

    public async Task<Stream?> OpenFileAsync(string jobId, string fileName, string tenantId, CancellationToken ct = default)
    {
        var record = await _jobs.GetAsync(tenantId, jobId, ct);
        if (record is null || IsGone(record) || record.Status != BulkExportStatus.Complete)
            return null;

        var file = record.Files.FirstOrDefault(f => string.Equals(f.Name, fileName, StringComparison.Ordinal));
        return file is null ? null : await _files.OpenAsync(file.Key, ct);
    }

    private bool IsGone(BulkExportJobRecord record)
        => record.Purged
           || (record.Status == BulkExportStatus.Complete && record.ExpiresAt is { } expires && expires <= _time.GetUtcNow());

    /// <summary>
    /// <c>_type</c>, or every supported type when it is absent. A type this
    /// export cannot contain is refused rather than silently left out, so a
    /// client never mistakes a missing type for "no data".
    /// </summary>
    internal static IReadOnlyList<string> ParseTypes(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return BulkExportOptions.SupportedTypes;

        var requested = type.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var unsupported = requested.Where(t => !BulkExportOptions.SupportedTypes.Contains(t, StringComparer.Ordinal)).ToList();
        if (unsupported.Count > 0)
            throw new BulkExportRequestException(
                "_type may only name " + string.Join(", ", BulkExportOptions.SupportedTypes) + ".");

        // Written in the supported order, whatever order they were asked in.
        return BulkExportOptions.SupportedTypes.Where(requested.Contains).ToList();
    }

    /// <summary><c>_since</c> as a FHIR instant: a date and time with a time zone.</summary>
    internal static DateTimeOffset? ParseSince(string? since)
    {
        if (string.IsNullOrWhiteSpace(since))
            return null;

        if (!InstantPattern().IsMatch(since)
            || !DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            throw new BulkExportRequestException("_since must be a FHIR instant, e.g. 2026-01-01T00:00:00Z.");

        return parsed.ToUniversalTime();
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex InstantPattern();
}
