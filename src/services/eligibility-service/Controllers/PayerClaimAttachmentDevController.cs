using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Responders;
using CloudHealthOffice.Infrastructure.Responders.Adapters;
using CloudHealthOffice.Infrastructure.Responders.Models;
using CloudHealthOffice.Infrastructure.Responders.Routing;
using Microsoft.AspNetCore.Mvc;

namespace EligibilityService.Controllers;

/// <summary>
/// Development-only inbound 275 ingress. Uses the production
/// <see cref="ICanonicalInboundClaimAttachmentAdapter"/> / receiver.
/// Disabled outside Development. Tenant from the body is ignored.
/// </summary>
[ApiController]
[Route("api/dev/payer")]
public sealed class PayerClaimAttachmentDevController : ControllerBase
{
    private readonly ICanonicalInboundClaimAttachmentAdapter _adapter;
    private readonly IInboundClaimAttachmentReceiptStore _receipts;
    private readonly IPayerEligibilityRouter _router;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<PayerClaimAttachmentDevController> _logger;

    public PayerClaimAttachmentDevController(
        ICanonicalInboundClaimAttachmentAdapter adapter,
        IInboundClaimAttachmentReceiptStore receipts,
        IPayerEligibilityRouter router,
        IHostEnvironment environment,
        ILogger<PayerClaimAttachmentDevController> logger)
    {
        _adapter = adapter;
        _receipts = receipts;
        _router = router;
        _environment = environment;
        _logger = logger;
    }

    [HttpPost("claims/{claimId}/attachments")]
    [RequestSizeLimit(64 * 1024 * 1024)]
    public async Task<IActionResult> Receive(
        string claimId,
        [FromForm] PayerClaimAttachmentDevForm form,
        CancellationToken ct)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        if (form.File is null || form.File.Length <= 0)
        {
            return BadRequest(new { error = "A non-empty file is required." });
        }

        var attachment = new InboundClaimAttachment
        {
            InboundAttachmentId = string.IsNullOrWhiteSpace(form.AttachmentId)
                ? Guid.NewGuid().ToString("N")
                : form.AttachmentId.Trim(),
            ExternalTransactionId = form.ExternalTransactionId,
            CorrelationId = form.CorrelationId,
            ClaimedTenantId = form.ClaimedTenantId,
            PayerId = form.PayerId,
            TradingPartnerId = form.TradingPartnerId,
            ClaimId = claimId,
            ClaimControlNumber = form.ClaimControlNumber,
            PatientControlNumber = form.PatientControlNumber,
            ServiceLineNumber = form.ServiceLineNumber,
            ServiceLineControlNumber = form.ServiceLineControlNumber,
            AttachmentControlNumber = form.AttachmentControlNumber,
            PayerRequestControlNumber = form.PayerRequestControlNumber,
            AttachmentType = form.AttachmentType,
            Mode = form.Mode,
            FileName = form.File.FileName,
            ContentType = string.IsNullOrWhiteSpace(form.ContentType) ? form.File.ContentType : form.ContentType,
            ContentLength = form.File.Length,
            SuppliedChecksumSha256 = form.ChecksumSha256,
            ReceivedAt = DateTimeOffset.UtcNow
        };

        await using var stream = form.File.OpenReadStream();
        var envelope = await _adapter.ProcessAsync(attachment, stream, ct);

        _logger.LogInformation(
            "Dev inbound attachment receipt={ReceiptId} transportSuccess={Success} status={Status}",
            envelope.Result?.ReceiptId,
            envelope.IsSuccess,
            envelope.Result?.Status);

        if (!envelope.IsSuccess)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, envelope);
        }

        return StatusCode(StatusCodes.Status202Accepted, envelope);
    }

    [HttpGet("claims/{claimId}/attachments")]
    public async Task<IActionResult> List(string claimId, [FromQuery] string? payerId, CancellationToken ct)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        var route = _router.ResolveIdentity(payerId, tradingPartnerId: null, authenticatedEndpointId: null);
        if (!route.IsResolved)
        {
            return BadRequest(new { error = route.Message ?? "Payer identifier is required." });
        }

        var list = await _receipts.ListByClaimIdAsync(route.TenantId!, claimId, ct);
        return Ok(list);
    }
}

public sealed class PayerClaimAttachmentDevForm
{
    public IFormFile? File { get; set; }

    public string? AttachmentId { get; set; }

    public string? ExternalTransactionId { get; set; }

    public string? CorrelationId { get; set; }

    public string? ClaimedTenantId { get; set; }

    public string? PayerId { get; set; }

    public string? TradingPartnerId { get; set; }

    public string? ClaimControlNumber { get; set; }

    public string? PatientControlNumber { get; set; }

    public int? ServiceLineNumber { get; set; }

    public string? ServiceLineControlNumber { get; set; }

    public string? AttachmentControlNumber { get; set; }

    public string? PayerRequestControlNumber { get; set; }

    public ClaimAttachmentType AttachmentType { get; set; } = ClaimAttachmentType.Other;

    public ClaimAttachmentMode Mode { get; set; } = ClaimAttachmentMode.Unsolicited;

    public string? ContentType { get; set; }

    public string? ChecksumSha256 { get; set; }
}
