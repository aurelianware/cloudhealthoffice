using IdCardService.Models;
using IdCardService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace IdCardService.Controllers;

[Route("api/v1/id-cards")]
[Authorize(Policy = "ProviderJwt")]
public class IdCardScanController : TenantAwareControllerBase
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

        // Cross-check the embedded tenant against the request tenant to stop
        // a card from one tenant being scanned against another's endpoint.
        if (!string.Equals(payload.TenantId, TenantId, StringComparison.OrdinalIgnoreCase))
        {
            return Problem(ScanErrorCodes.InvalidSignature, "Tenant mismatch on card payload");
        }

        var record = await _orchestrator.GetByCardIdAsync(TenantId, payload.CardId, ct);
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
        var coverage = await _coverage.GetActiveAsync(TenantId, record.MemberId, ct);
        if (coverage == null || !coverage.IsActive)
        {
            return StatusCode(StatusCodes.Status409Conflict, new
            {
                code = ScanErrorCodes.CoverageInactive,
                message = "Coverage is not active at scan time"
            });
        }

        await _orchestrator.RecordScanAsync(TenantId, payload.CardId, ct);

        var snapshot = await _eligibility.GetSnapshotAsync(TenantId, record.MemberId, request.ProviderNpi, ct);

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
