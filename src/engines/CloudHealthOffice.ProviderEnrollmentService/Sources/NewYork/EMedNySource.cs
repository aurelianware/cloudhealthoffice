using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.ProviderEnrollmentService.Cache;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.ProviderEnrollmentService.Sources.NewYork;

/// <summary>
/// New York eMedNY (Medicaid Management Information System).
/// Operated by Maximus on behalf of NYS DOH.
///
/// Integration status: STUB — implement against eMedNY Provider Directory API.
/// Docs: https://www.emedny.org/info/ProviderEnrollment/
///
/// Notes:
///   - New York uses an 8-digit MMIS provider ID alongside NPI
///   - Managed Long-Term Care (MLTC) and Health Homes are distinct LOBs
///   - NYC has separate enrollment tracks for some provider types
///   - Batch: eMedNY posts weekly provider file to registered MCO FTP accounts
/// </summary>
public sealed class EMedNySource : IStateEnrollmentSource
{
    public string StateCode         => "NY";
    public string SourceSystemName  => "eMedNY";
    public LineOfBusiness SupportedLobs =>
        LineOfBusiness.Medicaid |
        LineOfBusiness.CHIP     |
        LineOfBusiness.LTSS     |
        LineOfBusiness.BehavioralHealth;

    private readonly HttpClient _http;
    private readonly IEnrollmentRepository _cache;
    private readonly ILogger<EMedNySource> _logger;

    public EMedNySource(HttpClient http, IEnrollmentRepository cache, ILogger<EMedNySource> logger)
    {
        _http  = http;
        _cache = cache;
        _logger = logger;
    }

    public Task<StateEnrollmentRecord?> GetEnrollmentAsync(
        string npi, DateOnly asOfDate, CancellationToken ct = default)
    {
        _logger.LogWarning("NY eMedNY adapter not yet implemented for NPI {Npi}", SanitizeForLog(npi));
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
