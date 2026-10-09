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
    /// <exception cref="ArgumentException">The quarter label or setting is invalid.</exception>
    /// <exception cref="InvalidDataException">The file holds no rows in the CMS layout.</exception>
    Task<NcciLoadResult> LoadAsync(NcciLoadRequest request, Stream content, CancellationToken ct = default);
}

/// <summary>
/// <para><b>Idempotent.</b> Every row gets a stable document id
/// (tenant, setting, codes, effective date), so loading the same file twice
/// writes the same documents. On top of that, a ledger row per
/// (tenant, quarter, file kind, setting) records the file's SHA-256; a
/// re-run with an identical file returns <c>AlreadyLoaded</c> and writes
/// nothing unless <see cref="NcciLoadRequest.Force"/> is set.</para>
///
/// <para><b>Versioned by quarter.</b> PTP rows carry their own CMS effective
/// and deletion dates. MUE rows carry none, so they take the quarter's first
/// day as their effective date; earlier quarters' rows stay in place for
/// claims with earlier dates of service, and the lookup picks the latest row
/// in effect. A code the new MUE table no longer lists is terminated as of the
/// quarter start. <see cref="NcciTableVersion"/> moves forward to the loaded
/// quarter (never backward when an older quarter is back-filled).</para>
/// </summary>
internal sealed partial class NcciQuarterlyLoader : INcciQuarterlyLoader
{
    private const int MaxReportedRejections = 20;

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
                $"Unknown NCCI setting '{request.Setting}'. Use {NcciSettings.Practitioner} or {NcciSettings.OutpatientHospital}.",
                nameof(request));

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var part = string.IsNullOrWhiteSpace(request.Part) ? null : request.Part.Trim();
        if (part is not null && !PartRegex().IsMatch(part))
            throw new ArgumentException($"Part must be 1-16 letters or digits, got '{request.Part}'.", nameof(request));

        var ledgerId = NcciLoadRecord.MakeId(request.TenantId, request.Quarter, request.FileKind, setting, part);
        var previous = await _repository.GetLoadRecordAsync(request.TenantId, ledgerId, ct);
        if (previous is not null && previous.Sha256 == sha256 && !request.Force)
        {
            _logger.LogInformation(
                "NCCI {Kind} {Setting} file for {Quarter} already loaded (sha256 {Sha}); skipping",
                request.FileKind, setting, request.Quarter, sha256);
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
            };
        }

        // CMS files are Latin-1/Windows-1252 text in practice; codes and
        // dates are ASCII either way, and only rationale text could differ.
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.Latin1, detectEncodingFromByteOrderMarks: true);

        int loaded, rejected, expired = 0;
        IReadOnlyList<string> rejections;

        if (request.FileKind == NcciCmsFileKind.Ptp)
        {
            var parsed = CmsNcciFileParser.ParsePtp(reader);
            EnsureRows(parsed.Rows.Count, request);
            var pairs = parsed.Rows.Select(row => new NcciEditPair
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
            }).ToList();

            await _repository.UpsertQuarterAsync(request.TenantId, request.Quarter, pairs, [], ct);
            loaded = pairs.Count;
            rejected = parsed.Rejections.Count;
            rejections = parsed.Rejections.Take(MaxReportedRejections).ToList();
        }
        else
        {
            var parsed = CmsNcciFileParser.ParseMue(reader);
            EnsureRows(parsed.Rows.Count, request);
            var isPractitioner = setting == NcciSettings.Practitioner;
            var entries = parsed.Rows
                // A code listed twice keeps its last row, so ids stay unique.
                .GroupBy(row => row.ProcedureCode)
                .Select(g => g.Last())
                .Select(row => new MueEntry
                {
                    Id = MakeMueId(request.TenantId, setting, row.ProcedureCode, quarterStart),
                    TenantId = request.TenantId,
                    ProcedureCode = row.ProcedureCode,
                    MaxUnits = row.MaxUnits,
                    AdjudicationIndicator = row.AdjudicationIndicator,
                    AppliesToProfessional = isPractitioner,
                    AppliesToOutpatientFacility = !isPractitioner,
                    EffectiveDate = quarterStart,
                    TerminationDate = null,
                    Setting = setting,
                    Rationale = row.Rationale,
                    SourceQuarter = request.Quarter,
                }).ToList();

            await _repository.UpsertQuarterAsync(request.TenantId, request.Quarter, [], entries, ct);
            if (part is null)
            {
                expired = await _repository.ExpireMueEntriesAsync(
                    request.TenantId, setting, quarterStart,
                    entries.Select(e => e.ProcedureCode).ToHashSet(StringComparer.Ordinal), ct);
            }
            loaded = entries.Count;
            rejected = parsed.Rejections.Count;
            rejections = parsed.Rejections.Take(MaxReportedRejections).ToList();
        }

        await _repository.SaveLoadRecordAsync(new NcciLoadRecord
        {
            Id = ledgerId,
            TenantId = request.TenantId,
            Quarter = request.Quarter,
            FileKind = request.FileKind,
            Setting = setting,
            Part = part,
            FileName = request.FileName,
            Sha256 = sha256,
            RowsLoaded = loaded,
            RowsRejected = rejected,
            RowsExpired = expired,
            LoadedAt = DateTime.UtcNow,
            LoadedBy = request.LoadedBy,
        }, ct);

        await AdvanceVersionAsync(request.TenantId, request.Quarter, quarterStart, ct);
        _lookupCache.InvalidateTenant(request.TenantId);

        _logger.LogInformation(
            "NCCI {Kind} {Setting} load for {Quarter}: {Loaded} rows loaded, {Rejected} rejected, {Expired} expired",
            request.FileKind, setting, request.Quarter, loaded, rejected, expired);

        return new NcciLoadResult
        {
            Quarter = request.Quarter,
            FileKind = request.FileKind,
            Setting = setting,
            Sha256 = sha256,
            RowsLoaded = loaded,
            RowsRejected = rejected,
            RowsExpired = expired,
            Rejections = rejections,
        };
    }

    /// <summary>"2026Q4" → 2026-10-01 (UTC).</summary>
    public static DateTime ParseQuarter(string? quarter)
    {
        var match = quarter is null ? Match.Empty : QuarterRegex().Match(quarter);
        if (!match.Success)
            throw new ArgumentException($"Quarter must look like 2026Q4, got '{quarter}'.", nameof(quarter));

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
            throw new InvalidDataException(
                $"No {request.FileKind} rows in the CMS layout were found in '{request.FileName ?? "the file"}'.");
    }

    private async Task AdvanceVersionAsync(string tenantId, string quarter, DateTime quarterStart, CancellationToken ct)
    {
        var current = await _repository.GetCurrentVersionAsync(tenantId, ct);
        if (current is not null && string.CompareOrdinal(quarter, current.Quarter) < 0)
            return;

        var records = await _repository.ListLoadRecordsAsync(tenantId, quarter, ct);
        var pairCount = records.Where(r => r.FileKind == NcciCmsFileKind.Ptp).Sum(r => r.RowsLoaded);
        var mueCount = records.Where(r => r.FileKind == NcciCmsFileKind.Mue).Sum(r => r.RowsLoaded);

        await _repository.SaveVersionAsync(new NcciTableVersion
        {
            TenantId = tenantId,
            Quarter = quarter,
            ImportedAt = DateTime.UtcNow,
            NcciPairCount = pairCount,
            MueEntryCount = mueCount,
            EffectiveDate = quarterStart,
        }, ct);
    }

    [GeneratedRegex(@"^(\d{4})Q([1-4])$")]
    private static partial Regex QuarterRegex();

    [GeneratedRegex(@"^[A-Za-z0-9]{1,16}$")]
    private static partial Regex PartRegex();
}
