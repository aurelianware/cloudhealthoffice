using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using ProviderService.Models;
using ProviderService.Security;
using ProviderService.Services;

namespace ProviderService.Controllers;

/// <summary>
/// FL SMMC 3.0 MPIP (Managed Medical Assistance Physician Incentive Program)
/// endpoints for qualification management, bulk import, and pre-claim rate checks.
///
/// <para>
/// The tenant comes from the validated CHO token. The <c>{tenantId}</c> route
/// segment is kept for existing callers (claims-service's rate check) but only
/// as an echo of the token's tenant: a different path tenant is refused with
/// 403 and the service is never called. Reads take providers:read; the
/// qualification writes set enhanced payment-rate multipliers, so they need
/// contracts:write.
/// </para>
/// </summary>
[ApiController]
[Route("api/mpip")]
[Produces("application/json")]
public class MpipController : ControllerBase
{
    private const string QualificationWrite = "contracts:write";

    private readonly IMpipRateService _mpipService;
    private readonly ILogger<MpipController> _logger;

    public MpipController(
        IMpipRateService mpipService,
        ILogger<MpipController> logger)
    {
        _mpipService = mpipService;
        _logger = logger;
    }

    /// <summary>
    /// List qualified MPIP providers for a tenant and period.
    /// Defaults to the current FL fiscal year if period is not specified.
    /// </summary>
    [HttpGet("{tenantId}/providers")]
    [ProducesResponseType(typeof(IEnumerable<MpipProviderQualification>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<MpipProviderQualification>>> GetProviders(
        string tenantId,
        [FromQuery] string? period = null)
    {
        if (PathTenantMismatch(tenantId) is { } refused) return refused;
        tenantId = this.TokenTenantId();
        var effectivePeriod = period ?? MpipRateService.GetFiscalYearPeriod(DateTime.UtcNow);

        _logger.LogInformation(
            "Listing MPIP qualifications for tenant {TenantId}, period {Period}",
            SanitizeForLog(tenantId), SanitizeForLog(effectivePeriod));

        var providers = await _mpipService.GetQualifiedProvidersAsync(tenantId, effectivePeriod);
        return Ok(providers);
    }

    /// <summary>
    /// Get a single provider's MPIP qualification for a period.
    /// </summary>
    [HttpGet("{tenantId}/providers/{providerId}")]
    [ProducesResponseType(typeof(MpipProviderQualification), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MpipProviderQualification>> GetProviderQualification(
        string tenantId,
        string providerId,
        [FromQuery] string? period = null)
    {
        if (PathTenantMismatch(tenantId) is { } refused) return refused;
        tenantId = this.TokenTenantId();
        var effectivePeriod = period ?? MpipRateService.GetFiscalYearPeriod(DateTime.UtcNow);

        _logger.LogInformation(
            "Fetching MPIP qualification for provider {ProviderId}, tenant {TenantId}, period {Period}",
            SanitizeForLog(providerId), SanitizeForLog(tenantId), SanitizeForLog(effectivePeriod));

        var qualification = await _mpipService.GetQualificationAsync(providerId, tenantId, effectivePeriod);

        if (qualification is null)
        {
            return NotFound(new { message = $"No MPIP qualification found for provider '{providerId}' in period '{effectivePeriod}'" });
        }

        return Ok(qualification);
    }

    /// <summary>
    /// Create or update a provider's MPIP qualification (admin only).
    /// </summary>
    [HttpPut("{tenantId}/providers/{providerId}")]
    [RequirePermission(QualificationWrite)]
    [ProducesResponseType(typeof(MpipProviderQualification), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<MpipProviderQualification>> UpsertQualification(
        string tenantId,
        string providerId,
        [FromBody] MpipProviderQualification qualification)
    {
        if (PathTenantMismatch(tenantId) is { } refused) return refused;
        tenantId = this.TokenTenantId();
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        qualification.TenantId = tenantId;
        qualification.ProviderId = providerId;

        _logger.LogInformation(
            "Upserting MPIP qualification for provider {ProviderId}, tenant {TenantId}, " +
            "period {Period}, type={Type}, qualified={Qualified}",
            SanitizeForLog(providerId), SanitizeForLog(tenantId),
            SanitizeForLog(qualification.QualificationPeriod),
            qualification.ProviderType, qualification.IsQualified);

        await _mpipService.UpsertQualificationAsync(qualification);
        return Ok(qualification);
    }

    /// <summary>
    /// Bulk import MPIP qualifications from the AHCA qualified provider list (Oct 1 annual update).
    /// Accepts a JSON array of <see cref="MpipProviderQualification"/> records.
    /// </summary>
    [HttpPost("{tenantId}/bulk-import")]
    [RequirePermission(QualificationWrite)]
    [ProducesResponseType(typeof(BulkImportResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BulkImportResult>> BulkImport(
        string tenantId,
        [FromBody] List<MpipProviderQualification> qualifications)
    {
        if (PathTenantMismatch(tenantId) is { } refused) return refused;
        tenantId = this.TokenTenantId();
        if (qualifications.Count == 0)
        {
            return BadRequest(new { message = "Import list is empty" });
        }

        _logger.LogInformation(
            "Bulk importing {Count} MPIP qualifications for tenant {TenantId}",
            qualifications.Count, SanitizeForLog(tenantId));

        var imported = 0;
        var errors = new List<string>();

        foreach (var qualification in qualifications)
        {
            try
            {
                qualification.TenantId = tenantId;
                await _mpipService.UpsertQualificationAsync(qualification);
                imported++;
            }
            catch (Exception ex)
            {
                var msg = $"Provider {qualification.ProviderId}: {ex.Message}";
                errors.Add(msg);
                _logger.LogWarning(ex, "MPIP bulk import error for provider {ProviderId}", SanitizeForLog(qualification.ProviderId));
            }
        }

        _logger.LogInformation(
            "MPIP bulk import complete for tenant {TenantId}: {Imported}/{Total} imported, {Errors} errors",
            SanitizeForLog(tenantId), imported, qualifications.Count, errors.Count);

        return Ok(new BulkImportResult
        {
            TotalSubmitted = qualifications.Count,
            Imported = imported,
            Errors = errors
        });
    }

    /// <summary>
    /// Pre-claim rate verification: returns the MPIP multiplier that would apply
    /// for a given provider, service date, and member age.
    /// </summary>
    [HttpGet("{tenantId}/rate-check")]
    [ProducesResponseType(typeof(RateCheckResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RateCheckResult>> RateCheck(
        string tenantId,
        [FromQuery] string providerId,
        [FromQuery] DateTime serviceDate,
        [FromQuery] int memberAge)
    {
        if (PathTenantMismatch(tenantId) is { } refused) return refused;
        tenantId = this.TokenTenantId();
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return BadRequest(new { message = "providerId is required" });
        }

        _logger.LogInformation(
            "MPIP rate check: provider {ProviderId}, tenant {TenantId}, " +
            "serviceDate {ServiceDate:yyyy-MM-dd}, memberAge {MemberAge}",
            SanitizeForLog(providerId), SanitizeForLog(tenantId), serviceDate, memberAge);

        var multiplier = await _mpipService.GetEnhancedRateMultiplierAsync(
            providerId, tenantId, serviceDate, memberAge);

        var period = MpipRateService.GetFiscalYearPeriod(serviceDate);

        return Ok(new RateCheckResult
        {
            ProviderId = providerId,
            TenantId = tenantId,
            ServiceDate = serviceDate,
            MemberAge = memberAge,
            QualificationPeriod = period,
            Multiplier = multiplier,
            EnhancedRateApplies = multiplier > 1.0m
        });
    }

    /// <summary>The route tenant is only accepted as an echo of the token's tenant.</summary>
    private ObjectResult? PathTenantMismatch(string pathTenantId)
    {
        var tokenTenant = this.TokenTenantId();
        if (string.Equals(pathTenantId, tokenTenant, StringComparison.Ordinal))
            return null;

        _logger.LogWarning(
            "MPIP request refused: path tenant {PathTenant} does not match the authenticated tenant {TokenTenant}",
            SanitizeForLog(pathTenantId), SanitizeForLog(tokenTenant));
        return StatusCode(StatusCodes.Status403Forbidden,
            new { message = "The tenant in the path does not match the authenticated tenant." });
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

/// <summary>
/// Result of a bulk MPIP qualification import.
/// </summary>
public class BulkImportResult
{
    public int TotalSubmitted { get; set; }
    public int Imported { get; set; }
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Pre-claim MPIP rate check result.
/// </summary>
public class RateCheckResult
{
    public string ProviderId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public DateTime ServiceDate { get; set; }
    public int MemberAge { get; set; }
    public string QualificationPeriod { get; set; } = string.Empty;
    public decimal Multiplier { get; set; }
    public bool EnhancedRateApplies { get; set; }
}
