using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CloudHealthOffice.NcciEngine.Domain;
using CloudHealthOffice.NcciEngine.Models;
using CloudHealthOffice.NcciEngine.Persistence;
using CloudHealthOffice.NcciEngine.Services;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.NcciEngine.Import;

/// <summary>
/// Loads one public CMS NCCI quarterly file (PTP or MUE, practitioner or
/// outpatient hospital) into the store <see cref="INcciEditService"/> reads.
/// </summary>
public interface INcciQuarterlyLoader
{
    /// <exception cref="ArgumentException">The quarter label, setting or part is invalid, or a PTP quarter older than one already loaded was given.</exception>
    /// <exception cref="InvalidDataException">The file holds no rows in the CMS layout.</exception>
    Task<NcciLoadResult> LoadAsync(NcciLoadRequest request, Stream content, CancellationToken ct = default);
}

/// <summary>
/// <para><b>Memory.</b> The upload is copied once to a temporary file while
/// its SHA-256 is computed, then parsed from that file as a stream. PTP rows
/// are written in batches of <see cref="PtpBatchSize"/>, so a million-row
/// practitioner table is never held in memory. MUE tables (~15k rows) are
/// read whole.</para>
///
/// <para><b>Idempotent.</b> Every row gets a stable document id
/// (tenant, setting, codes, effective date). A ledger row per
/// (tenant, quarter, file kind, setting, part) records the file's SHA-256 and
/// is written last; an identical re-run returns <c>AlreadyLoaded</c> and writes
/// nothing unless <see cref="NcciLoadRequest.Force"/> is set. If a load fails
/// part-way, no ledger row exists and the retry runs in full.</para>
///
/// <para><b>Snapshots.</b> Each file is a full snapshot of its slot:</para>
/// <list type="bullet">
///   <item>PTP: rows carry their CMS effective and deletion dates. After the
///   upsert, rows the same slot (setting + part) wrote earlier but this file no
///   longer lists are deleted (same quarter: a correction) or ended at the
///   quarter start (earlier quarter). PTP files carry full history, so loading
///   a quarter older than one already loaded for the setting is refused.</item>
///   <item>MUE: rows take the quarter's first day as effective date and end
///   at the next loaded quarter's start, if one exists. Rows of the same
///   quarter the file omits are deleted; earlier quarters' rows for codes the
///   file omits end at this quarter's start.</item>
///   <item>Once a CMS table is loaded for a setting, setting-less seed rows no
///   longer apply to that setting (<see cref="NcciTableVersion.PtpSettings"/>,
///   <see cref="NcciTableVersion.MueSettings"/>).</item>
/// </list>
///
/// <para><b>Cache.</b> Every load writes a new <see cref="NcciTableVersion.LoadStamp"/>.
/// Lookup caches in every process key entries by stamp, so all processes pick
/// up the load within <see cref="NcciLookupCache.VersionTtl"/>.</para>
/// </summary>
internal sealed partial class NcciQuarterlyLoader : INcciQuarterlyLoader
{
    private const int MaxReportedRejections = 20;
    internal const int PtpBatchSize = 5000;

    private readonly INcciRepository _repository;
    private readonly NcciLookupCache _lookupCache;
    private readonly ILogger<NcciQuarterlyLoader> _logger;

    public NcciQuarterlyLoader(
        INcciRepository repository,
        NcciLookupCache lookupCache,
        ILogger<NcciQuarterlyLoader> logger)
    {
        _repository = repository;
        _lookupCache = lookupCache;
        _logger = logger;
    }

    public async Task<NcciLoadResult> LoadAsync(NcciLoadRequest request, Stream content, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TenantId);
        var quarterStart = ParseQuarter(request.Quarter);
        if (!NcciSettings.TryParse(request.Setting, out var setting))
            throw new ArgumentException(
                $"Unknown NCCI setting. Use {NcciSettings.Practitioner} or {NcciSettings.OutpatientHospital}.",
                nameof(request));

        var part = string.IsNullOrWhiteSpace(request.Part) ? null : request.Part.Trim();
        if (part is not null && !PartRegex().IsMatch(part))
            throw new ArgumentException("Part must be 1-16 letters or digits.", nameof(request));

        var ledger = await _repository.ListLoadRecordsAsync(request.TenantId, null, ct);
        if (request.FileKind == NcciCmsFileKind.Ptp
            && ledger.Any(r => r.FileKind == NcciCmsFileKind.Ptp && r.Setting == setting
                               && string.CompareOrdinal(r.Quarter, request.Quarter) > 0))
        {
            throw new ArgumentException(
                "A newer PTP quarter is already loaded for this setting. PTP files carry full edit history " +
                "(effective and deletion dates), so older quarters do not need to be back-filled.",
                nameof(request));
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"ncci-load-{Guid.NewGuid():N}.tmp");
        await using var temp = new FileStream(
            tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        var sha256 = await CopyAndHashAsync(content, temp, ct);
        temp.Position = 0;

        var ledgerId = NcciLoadRecord.MakeId(request.TenantId, request.Quarter, request.FileKind, setting, part);
        var previous = ledger.FirstOrDefault(r => r.Id == ledgerId);
        if (previous is not null && previous.Sha256 == sha256 && !request.Force)
        {
            _logger.LogInformation(
                "NCCI {Kind} {Setting} file for {Quarter} already loaded (sha256 {Sha}); skipping",
                request.FileKind, SanitizeForLog(setting), SanitizeForLog(request.Quarter), sha256);
            return new NcciLoadResult
            {
                Quarter = request.Quarter,
                FileKind = request.FileKind,
                Setting = setting,
                Sha256 = sha256,
                AlreadyLoaded = true,
                RowsLoaded = previous.RowsLoaded,
                RowsRejected = previous.RowsRejected,
                RowsExpired = previous.RowsExpired,
                RowsDeleted = previous.RowsDeleted,
            };
        }

        // CMS files are Latin-1/Windows-1252 text in practice; codes and
        // dates are ASCII either way, and only rationale text could differ.
        using var reader = new StreamReader(temp, Encoding.Latin1, detectEncodingFromByteOrderMarks: true, bufferSize: 81920, leaveOpen: true);
        var rejections = new CmsRejectionLog();

        var outcome = request.FileKind == NcciCmsFileKind.Ptp
            ? await LoadPtpAsync(request, setting, part, quarterStart, reader, rejections, ct)
            : await LoadMueAsync(request, setting, part, quarterStart, reader, rejections, ledger, ct);

        var record = new NcciLoadRecord
        {
            Id = ledgerId,
            TenantId = request.TenantId,
            Quarter = request.Quarter,
            FileKind = request.FileKind,
            Setting = setting,
            Part = part,
            FileName = request.FileName,
            Sha256 = sha256,
            RowsLoaded = outcome.Loaded,
            RowsRejected = rejections.Count,
            RowsExpired = outcome.Expired,
            RowsDeleted = outcome.Deleted,
            LoadedAt = DateTime.UtcNow,
            LoadedBy = request.LoadedBy,
        };

        // Version before ledger: if the version write fails, there is no
        // ledger row and a retry reloads instead of reporting AlreadyLoaded.
        await SaveVersionAsync(request.TenantId, request.Quarter, quarterStart, request.FileKind, setting, ledger, record, ct);
        await _repository.SaveLoadRecordAsync(record, ct);
        _lookupCache.InvalidateTenant(request.TenantId);

        _logger.LogInformation(
            "NCCI {Kind} {Setting} load for {Quarter}: {Loaded} rows loaded, {Rejected} rejected, {Expired} expired, {Deleted} deleted",
            request.FileKind, SanitizeForLog(setting), SanitizeForLog(request.Quarter),
            outcome.Loaded, rejections.Count, outcome.Expired, outcome.Deleted);

        return new NcciLoadResult
        {
            Quarter = request.Quarter,
            FileKind = request.FileKind,
            Setting = setting,
            Sha256 = sha256,
            RowsLoaded = outcome.Loaded,
            RowsRejected = rejections.Count,
            RowsExpired = outcome.Expired,
            RowsDeleted = outcome.Deleted,
            Rejections = rejections.Sample.Take(MaxReportedRejections).ToList(),
        };
    }

    private sealed record LoadOutcome(int Loaded, int Expired, int Deleted);

    private async Task<LoadOutcome> LoadPtpAsync(
        NcciLoadRequest request, string setting, string? part, DateTime quarterStart,
        TextReader reader, CmsRejectionLog rejections, CancellationToken ct)
    {
        var loadId = Guid.NewGuid().ToString("N");
        var sourceKey = $"{setting}:{part}";
        var batch = new List<NcciEditPair>(PtpBatchSize);
        var loaded = 0;

        foreach (var row in CmsNcciFileParser.ReadPtp(reader, rejections))
        {
            batch.Add(new NcciEditPair
            {
                Id = MakePairId(request.TenantId, setting, row.Column1Code, row.Column2Code, row.EffectiveDate),
                TenantId = request.TenantId,
                Column1Code = row.Column1Code,
                Column2Code = row.Column2Code,
                ModifierIndicator = row.ModifierIndicator,
                PolicyType = ClassifyPolicy(row.Rationale),
                EffectiveDate = row.EffectiveDate,
                TerminationDate = row.DeletionDate,
                Setting = setting,
                Rationale = row.Rationale,
                ExistedPrior1996 = row.ExistedPrior1996,
                SourceQuarter = request.Quarter,
                SourceKey = sourceKey,
                LoadId = loadId,
            });

            if (batch.Count == PtpBatchSize)
            {
                await _repository.UpsertQuarterAsync(request.TenantId, request.Quarter, batch, [], ct);
                loaded += batch.Count;
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await _repository.UpsertQuarterAsync(request.TenantId, request.Quarter, batch, [], ct);
            loaded += batch.Count;
        }

        EnsureRows(loaded, request);

        var (expired, deleted) = await _repository.ReconcilePtpSnapshotAsync(
            request.TenantId, sourceKey, request.Quarter, quarterStart, loadId, ct);
        return new LoadOutcome(loaded, expired, deleted);
    }

    private async Task<LoadOutcome> LoadMueAsync(
        NcciLoadRequest request, string setting, string? part, DateTime quarterStart,
        TextReader reader, CmsRejectionLog rejections, IReadOnlyList<NcciLoadRecord> ledger, CancellationToken ct)
    {
        var rows = CmsNcciFileParser.ReadMue(reader, rejections)
            // A code listed twice keeps its last row, so ids stay unique.
            .GroupBy(row => row.ProcedureCode)
            .Select(g => g.Last())
            .ToList();
        EnsureRows(rows.Count, request);

        // The snapshot is in effect until the next loaded quarter's table starts.
        var nextStart = ledger
            .Where(r => r.FileKind == NcciCmsFileKind.Mue && r.Setting == setting && r.Part is null
                        && string.CompareOrdinal(r.Quarter, request.Quarter) > 0)
            .Select(r => (DateTime?)ParseQuarter(r.Quarter))
            .Min();

        var isPractitioner = setting == NcciSettings.Practitioner;
        var entries = rows.Select(row => new MueEntry
        {
            Id = MakeMueId(request.TenantId, setting, row.ProcedureCode, quarterStart),
            TenantId = request.TenantId,
            ProcedureCode = row.ProcedureCode,
            MaxUnits = row.MaxUnits,
            AdjudicationIndicator = row.AdjudicationIndicator,
            AppliesToProfessional = isPractitioner,
            AppliesToOutpatientFacility = !isPractitioner,
            EffectiveDate = quarterStart,
            TerminationDate = nextStart,
            Setting = setting,
            Rationale = row.Rationale,
            SourceQuarter = request.Quarter,
        }).ToList();

        await _repository.UpsertQuarterAsync(request.TenantId, request.Quarter, [], entries, ct);

        // A part-file is not a full table: another part may list the codes it omits.
        if (part is not null)
            return new LoadOutcome(entries.Count, 0, 0);

        var (expired, deleted) = await _repository.ReconcileMueSnapshotAsync(
            request.TenantId, setting, quarterStart,
            entries.Select(e => e.ProcedureCode).ToHashSet(StringComparer.Ordinal), ct);
        return new LoadOutcome(entries.Count, expired, deleted);
    }

    private static async Task<string> CopyAndHashAsync(Stream source, Stream destination, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            await destination.FlushAsync(ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>"2026Q4" → 2026-10-01 (UTC).</summary>
    public static DateTime ParseQuarter(string? quarter)
    {
        var match = quarter is null ? Match.Empty : QuarterRegex().Match(quarter);
        if (!match.Success)
            throw new ArgumentException("Quarter must look like 2026Q4.", nameof(quarter));

        var year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var q = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return new DateTime(year, (q - 1) * 3 + 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    public static string MakePairId(string tenantId, string setting, string col1, string col2, DateTime effective)
        => $"{tenantId}_{setting}_{col1}_{col2}_{effective:yyyyMMdd}";

    public static string MakeMueId(string tenantId, string setting, string code, DateTime effective)
        => $"{tenantId}_{setting}_{code}_{effective:yyyyMMdd}";

    private static NcciPolicyType ClassifyPolicy(string? rationale) =>
        rationale is not null && rationale.Contains("mutually exclusive", StringComparison.OrdinalIgnoreCase)
            ? NcciPolicyType.MutuallyExclusive
            : NcciPolicyType.ProcedureToProc;

    private static void EnsureRows(int count, NcciLoadRequest request)
    {
        if (count == 0)
            throw new InvalidDataException($"No {request.FileKind} rows in the CMS layout were found in the file.");
    }

    private async Task SaveVersionAsync(
        string tenantId, string quarter, DateTime quarterStart, NcciCmsFileKind kind, string setting,
        IReadOnlyList<NcciLoadRecord> ledger, NcciLoadRecord current, CancellationToken ct)
    {
        var version = await _repository.GetCurrentVersionAsync(tenantId, ct)
                      ?? new NcciTableVersion { TenantId = tenantId };
        version.Id = NcciTableVersion.CurrentId;
        version.TenantId = tenantId;
        version.LoadStamp = Guid.NewGuid().ToString("N");

        var settings = kind == NcciCmsFileKind.Ptp ? version.PtpSettings : version.MueSettings;
        if (!settings.Contains(setting)) settings.Add(setting);

        // Quarter and counts only move forward; a back-filled older quarter
        // still changes the stamp (its rows are new) but not the headline.
        if (string.IsNullOrEmpty(version.Quarter) || string.CompareOrdinal(quarter, version.Quarter) >= 0)
        {
            var records = ledger.Where(r => r.Quarter == quarter && r.Id != current.Id).Append(current).ToList();
            version.Quarter = quarter;
            version.ImportedAt = DateTime.UtcNow;
            version.EffectiveDate = quarterStart;
            version.NcciPairCount = records.Where(r => r.FileKind == NcciCmsFileKind.Ptp).Sum(r => r.RowsLoaded);
            version.MueEntryCount = records.Where(r => r.FileKind == NcciCmsFileKind.Mue).Sum(r => r.RowsLoaded);
        }

        await _repository.SaveVersionAsync(version, ct);
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);

    [GeneratedRegex(@"^(\d{4})Q([1-4])$")]
    private static partial Regex QuarterRegex();

    [GeneratedRegex(@"^[A-Za-z0-9]{1,16}$")]
    private static partial Regex PartRegex();
}
