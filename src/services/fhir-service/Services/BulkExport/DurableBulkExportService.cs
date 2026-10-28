using System.Globalization;
using System.Text.RegularExpressions;
using FhirService.Models;
using FhirService.Services.PayerToPayer.BulkMemberMatch;
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
/// Group did not exist. <c>Patient/$export</c> exports every patient the
/// caller may see, which for a provider is that same panel. A system-level
/// <c>$export</c> (every member in the tenant) is still refused as not
/// implemented: it has no approved caller.
/// </para>
///
/// <para>
/// <c>PayerToPayer/$export</c> is the Payer-to-Payer bulk transfer (G-35): the
/// requesting payer's members, those whose consent naming that payer is in
/// force, each exported as the single-member <c>$member-data-export</c> would.
/// Started as PDex <c>Group/{id}/$davinci-data-export</c>, it covers only the
/// members of that Group, the matched Group a <c>$bulk-member-match</c> by the
/// same client answered with.
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
    private readonly IPayerToPayerMatchGroupStore? _matchGroups;

    public DurableBulkExportService(
        IBulkExportJobStore jobs, IBulkExportFileStore files, IOptions<BulkExportOptions> options, TimeProvider time,
        IPayerToPayerMatchGroupStore? matchGroups = null)
    {
        _jobs = jobs;
        _files = files;
        _options = options;
        _time = time;
        _matchGroups = matchGroups;
    }

    public async Task<BulkExportJob> InitiateExportAsync(
        BulkExportRequest request, string tenantId, CancellationToken ct = default)
    {
        switch (request.Level)
        {
            case BulkExportLevel.Group:
                if (string.IsNullOrWhiteSpace(request.RequestedBy)
                    || !string.Equals(request.GroupId, request.RequestedBy, StringComparison.Ordinal))
                    throw new BulkExportGroupNotFoundException();
                break;

            // Every patient the caller may export: for a provider, the same
            // attributed panel its own Group holds, decided member by member
            // when the export runs.
            case BulkExportLevel.Patient:
                if (string.IsNullOrWhiteSpace(request.RequestedBy))
                    throw new BulkExportRequestException("The caller could not be identified.");
                break;

            // Every member whose consent for the requesting payer is in force,
            // decided member by member when the export runs. The controller
            // has already bound the payer to the client's registration.
            case BulkExportLevel.PayerToPayer:
                if (string.IsNullOrWhiteSpace(request.RequestedBy) || string.IsNullOrWhiteSpace(request.ReceivingPayerId))
                    throw new BulkExportRequestException("The requesting payer could not be identified.");
                // A match Group: only the matched one, made for this client.
                if (request.GroupId is { } groupId)
                {
                    var group = _matchGroups is null
                        ? null
                        : await _matchGroups.GetAsync(tenantId, groupId, _time.GetUtcNow(), ct);
                    if (group is null
                        || group.Code != PayerToPayerBulkMemberMatchService.Matched
                        || !PayerToPayerBulkMemberMatchService.Owns(
                            new BulkMemberMatchCaller(tenantId, request.ReceivingPayerId, request.RequestedBy), group))
                        throw new BulkExportGroupNotFoundException();
                }
                break;

            default:
                throw new BulkExportNotImplementedException();
        }

        if (request.OutputFormat is { } format && !NdjsonFormats.Contains(format))
            throw new BulkExportRequestException("_outputFormat must be application/fhir+ndjson.");

        var types = ParseTypes(request.Type, BulkExportOptions.TypesFor(request.Level));
        // Checked now so a bad filter is a 400 at kick-off, not a failed job.
        BulkExportTypeFilter.Parse(request.TypeFilters, types);
        var patients = ParsePatients(request.Patients, request.Level);

        var record = new BulkExportJobRecord
        {
            TenantId = tenantId,
            JobId = Guid.NewGuid().ToString("N"),
            RequestedBy = request.RequestedBy!,
            Level = request.Level,
            GroupId = request.Level is BulkExportLevel.Group or BulkExportLevel.PayerToPayer ? request.GroupId : null,
            ReceivingPayerId = request.Level == BulkExportLevel.PayerToPayer ? request.ReceivingPayerId : null,
            Types = types,
            Since = ParseSince(request.Since),
            TypeFilters = request.TypeFilters.ToList(),
            Patients = patients,
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
        => ParseTypes(type, BulkExportOptions.SupportedTypes);

    internal static IReadOnlyList<string> ParseTypes(string? type, IReadOnlyList<string> supported)
    {
        if (string.IsNullOrWhiteSpace(type))
            return supported;

        var requested = type.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var unsupported = requested.Where(t => !supported.Contains(t, StringComparer.Ordinal)).ToList();
        if (unsupported.Count > 0)
            throw new BulkExportRequestException("_type may only name " + string.Join(", ", supported) + ".");

        // Written in the supported order, whatever order they were asked in.
        return supported.Where(requested.Contains).ToList();
    }

    /// <summary>
    /// The <c>patient</c> parameter as member ids: Group and Patient exports
    /// only, each a <c>Patient/{id}</c> reference. It narrows the export and
    /// never widens it: a patient outside the caller's scope is not exported.
    /// </summary>
    internal static IReadOnlyList<string>? ParsePatients(IReadOnlyList<string>? references, BulkExportLevel level)
    {
        if (references is null)
            return null;
        if (level is not (BulkExportLevel.Group or BulkExportLevel.Patient))
            throw new BulkExportRequestException("patient is supported on Group and Patient exports only.");
        if (references.Count == 0)
            throw new BulkExportRequestException("patient must name at least one Patient reference.");

        var ids = new List<string>();
        foreach (var reference in references)
        {
            var match = PatientReferencePattern().Match(reference ?? string.Empty);
            if (!match.Success)
                throw new BulkExportRequestException("patient must be a reference of the form Patient/{id}.");
            ids.Add(match.Groups[1].Value);
        }
        return ids.Distinct(StringComparer.Ordinal).ToList();
    }

    [GeneratedRegex(@"^Patient/([A-Za-z0-9\-\.]{1,64})$")]
    private static partial Regex PatientReferencePattern();

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
