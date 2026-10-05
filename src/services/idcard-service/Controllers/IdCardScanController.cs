using IdCardService.Middleware;
using IdCardService.Models;
using IdCardService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace IdCardService.Controllers;

/// <summary>
/// Provider-facing QR scan. Providers authenticate with their own JWT (the
/// external <c>ProviderJwt</c> scheme), not a CHO token, so there is no token
/// tenant. The tenant is the one inside the card payload, which this service
/// signed and verifies before using it; no header, query or unsigned body field
/// selects a tenant. Upstream calls go out with this service's own service
/// token for that tenant (see <see cref="UpstreamAuthorization"/>).
/// </summary>
[Route("api/v1/id-cards")]
[Authorize(Policy = IdCardAuth.ProviderJwtPolicy)]
public class IdCardScanController : ControllerBase
{
    private readonly IQrCodeService _qr;
    private readonly IIdCardOrchestrator _orchestrator;
    private readonly ICoverageClient _coverage;
    private readonly IEligibilityClient _eligibility;
    private readonly ILogger<IdCardScanController> _logger;

    public IdCardScanController(
        IQrCodeService qr,
        IIdCardOrchestrator orchestrator,
        ICoverageClient coverage,
        IEligibilityClient eligibility,
        ILogger<IdCardScanController> logger)
    {
        _qr = qr;
        _orchestrator = orchestrator;
        _coverage = coverage;
        _eligibility = eligibility;
        _logger = logger;
    }

    /// <summary>
    /// Scan a card QR and return a live 271 eligibility snapshot. Validates
    /// HMAC signature within the accepted key-version window, rejects revoked
    /// cards, and confirms coverage is active as-of scan time (no time-window
    /// check on issuedAt — the card is valid as long as coverage is and the
    /// signing key is in the rolling window).
    /// </summary>
    [HttpPost("scan")]
    [EnableRateLimiting("card-scan")]
    public async Task<IActionResult> Scan([FromBody] QrScanRequest request, CancellationToken ct)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.QrPayload))
        {
            return BadRequest(new { code = ScanErrorCodes.MalformedPayload, message = "qrPayload required" });
        }

        var (payload, errorCode, errorMessage) = await _qr.VerifyAsync(request.QrPayload, ct);
        if (payload == null)
        {
            return Problem(errorCode ?? ScanErrorCodes.InvalidSignature, errorMessage);
        }

        // The signed payload names the tenant. A caller that also carries a CHO
        // token (and so a token tenant) may only scan its own tenant's cards.
        var tenantId = payload.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return Problem(ScanErrorCodes.MalformedPayload, "Card payload names no tenant");
        }
        if (HttpContext.Items["TenantId"] is string tokenTenant
            && !string.Equals(tokenTenant, tenantId, StringComparison.Ordinal))
        {
            return Problem(ScanErrorCodes.InvalidSignature, "Tenant mismatch on card payload");
        }

        var record = await _orchestrator.GetByCardIdAsync(tenantId, payload.CardId, ct);
        if (record == null)
        {
            return Problem(ScanErrorCodes.UnknownCard, "Card not found");
        }
        if (record.RevokedAt.HasValue)
        {
            return StatusCode(StatusCodes.Status410Gone, new
            {
                code = ScanErrorCodes.Revoked,
                message = $"Card revoked ({record.RevocationReason})",
                revokedAt = record.RevokedAt
            });
        }

        // Coverage-anchored validity check: the card works as long as the
        // member has active coverage. Missing coverage → scan fails with a
        // specific code rather than a generic 401.
        CoverageDto? coverage;
        try
        {
            coverage = await _coverage.GetActiveAsync(tenantId, record.MemberId, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            // Not "coverage inactive": the answer is unknown.
            _logger.LogError(ex, "Coverage lookup failed during scan of card {CardId}", LogSafe.Of(payload.CardId));
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                code = ScanErrorCodes.UpstreamUnavailable,
                message = "Coverage could not be verified"
            });
        }
        if (coverage == null || !coverage.IsActive)
        {
            return StatusCode(StatusCodes.Status409Conflict, new
            {
                code = ScanErrorCodes.CoverageInactive,
                message = "Coverage is not active at scan time"
            });
        }

        await _orchestrator.RecordScanAsync(tenantId, payload.CardId, ct);

        object? snapshot;
        try
        {
            snapshot = await _eligibility.GetSnapshotAsync(tenantId, record.MemberId, request.ProviderNpi, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            _logger.LogError(ex, "Eligibility snapshot failed during scan of card {CardId}", LogSafe.Of(payload.CardId));
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                code = ScanErrorCodes.UpstreamUnavailable,
                message = "Eligibility could not be retrieved"
            });
        }

        return Ok(new QrScanResponse
        {
            CardId = record.CardId,
            MemberId = record.MemberId,
            TenantId = record.TenantId,
            IssuedAt = record.IssuedAt,
            ScannedAt = DateTime.UtcNow,
            CardActive = true,
            CoverageActive = true,
            EligibilitySnapshot = snapshot
        });
    }

    private IActionResult Problem(string code, string? message) =>
        StatusCode(StatusCodes.Status401Unauthorized, new { code, message });
}
