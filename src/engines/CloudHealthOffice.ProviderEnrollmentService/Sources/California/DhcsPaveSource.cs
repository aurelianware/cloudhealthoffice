using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.ProviderEnrollmentService.Cache;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.ProviderEnrollmentService.Sources.California;

/// <summary>
/// California DHCS PAVE (Provider Application and Validation for Enrollment).
/// Operated by DHCS — Medi-Cal managed care and FFS.
///
/// Integration status: STUB — implement against DHCS Web Services API v2.
/// Docs: https://www.dhcs.ca.gov/provgovpart/Pages/PAVE.aspx
///
/// Notes:
///   - PAVE uses NPI as the primary lookup key (same as PEMS)
///   - Medi-Cal has both FFS and managed care enrollment tracks —
///     both should be captured in SupportedLobs
///   - County-organized health systems (COHS) affect service areas
///   - Batch: DHCS drops monthly enrollment extracts via secure FTP
/// </summary>
public sealed class DhcsPaveSource : IStateEnrollmentSource
{
    public string StateCode         => "CA";
    public string SourceSystemName  => "PAVE";
    public LineOfBusiness SupportedLobs =>
        LineOfBusiness.Medicaid |
        LineOfBusiness.CHIP;

    private readonly HttpClient _http;
    private readonly IEnrollmentRepository _cache;
    private readonly ILogger<DhcsPaveSource> _logger;

    public DhcsPaveSource(
        HttpClient http,
        IEnrollmentRepository cache,
        ILogger<DhcsPaveSource> logger)
    {
        _http   = http;
        _cache  = cache;
        _logger = logger;
    }

    public Task<StateEnrollmentRecord?> GetEnrollmentAsync(
        string npi, DateOnly asOfDate, CancellationToken ct = default)
    {
        // TODO: implement against DHCS PAVE API
        // Pattern: check _cache first, call _http on miss, upsert result
        _logger.LogWarning("CA PAVE adapter not yet implemented for NPI {Npi}", SanitizeForLog(npi));
        return Task.FromResult<StateEnrollmentRecord?>(null);
    }

    public async Task<IReadOnlyList<StateEnrollmentRecord>> GetPanelAsync(
        IEnumerable<string> npis, DateOnly asOfDate, CancellationToken ct = default)
    {
        var tasks = npis.Select(npi => GetEnrollmentAsync(npi, asOfDate, ct));
        var results = await Task.WhenAll(tasks);
        return results.Where(r => r is not null).Cast<StateEnrollmentRecord>().ToList();
    }

    public Task<EnrollmentApplication?> GetApplicationStatusAsync(
        string applicationId, CancellationToken ct = default)
    {
        _logger.LogWarning("CA PAVE application lookup not yet implemented");
        return Task.FromResult<EnrollmentApplication?>(null);
    }

    public Task<BatchSyncResult> BulkSyncAsync(CancellationToken ct = default)
    {
        _logger.LogWarning("CA PAVE bulk sync not yet implemented");
        return Task.FromResult(new BatchSyncResult
        {
            StateCode        = StateCode,
            SourceSystem     = SourceSystemName,
            SyncStarted      = DateTime.UtcNow,
            SyncCompleted    = DateTime.UtcNow,
            RecordsProcessed = 0,
            RecordsUpserted  = 0,
            RecordsSkipped   = 0,
            Errors           = 0,
            ErrorDetails     = ["Not implemented"]
        });
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
