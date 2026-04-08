using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.ProviderEnrollmentService.Cache;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.ProviderEnrollmentService.Sources.Florida;

/// <summary>
/// Florida AHCA FMMIS (Florida Medicaid Management Information System).
/// Operated by DXC Technology on behalf of AHCA.
///
/// Integration status: STUB — implement against FMMIS Provider Web Services.
/// Docs: https://www.floridamedicaid.com/index.shtml/provider
///
/// Notes:
///   - Florida uses a 9-digit Medicaid provider ID alongside NPI
///   - SMMC (Statewide Medicaid Managed Care) plans require separate MCO contracting
///   - Batch: AHCA provides monthly provider file via secure portal download
/// </summary>
public sealed class AhcaFmmisSource : IStateEnrollmentSource
{
    public string StateCode         => "FL";
    public string SourceSystemName  => "FMMIS";
    public LineOfBusiness SupportedLobs =>
        LineOfBusiness.Medicaid |
        LineOfBusiness.CHIP     |
        LineOfBusiness.LTSS;

    private readonly HttpClient _http;
    private readonly IEnrollmentRepository _cache;
    private readonly ILogger<AhcaFmmisSource> _logger;

    public AhcaFmmisSource(HttpClient http, IEnrollmentRepository cache, ILogger<AhcaFmmisSource> logger)
    {
        _http  = http;
        _cache = cache;
        _logger = logger;
    }

    public Task<StateEnrollmentRecord?> GetEnrollmentAsync(
        string npi, DateOnly asOfDate, CancellationToken ct = default)
    {
        _logger.LogWarning("FL FMMIS adapter not yet implemented for NPI {Npi}", SanitizeForLog(npi));
        return Task.FromResult<StateEnrollmentRecord?>(null);
    }

    public async Task<IReadOnlyList<StateEnrollmentRecord>> GetPanelAsync(
        IEnumerable<string> npis, DateOnly asOfDate, CancellationToken ct = default)
    {
        var tasks = npis.Select(npi => GetEnrollmentAsync(npi, asOfDate, ct));
        var results = await Task.WhenAll(tasks);
        return results.Where(r => r is not null).Cast<StateEnrollmentRecord>().ToList();
    }

    public Task<EnrollmentApplication?> GetApplicationStatusAsync(string applicationId, CancellationToken ct = default)
        => Task.FromResult<EnrollmentApplication?>(null);

    public Task<BatchSyncResult> BulkSyncAsync(CancellationToken ct = default) =>
        Task.FromResult(new BatchSyncResult
        {
            StateCode = StateCode, SourceSystem = SourceSystemName,
            SyncStarted = DateTime.UtcNow, SyncCompleted = DateTime.UtcNow,
            ErrorDetails = ["Not implemented"]
        });

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
