using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Capabilities;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using Microsoft.AspNetCore.Mvc;

namespace EligibilityService.Controllers;

/// <summary>
/// Development-only 275 attachment upload against an existing claim
/// transmission. Disabled outside Development. Tenant is taken from
/// X-Tenant-ID / the original transmission — never from the multipart body.
/// </summary>
[ApiController]
[Route("api/dev/gateway")]
public sealed class GatewayClaimAttachmentDemoController : ControllerBase
{
    private readonly IHealthcareGatewayResolver _resolver;
    private readonly IClaimTransmissionStore _transmissions;
    private readonly IClaimAttachmentContentStore _content;
    private readonly IClaimAttachmentTransmissionStore _attachmentTransmissions;
    private readonly IHostEnvironment _environment;

    public GatewayClaimAttachmentDemoController(
        IHealthcareGatewayResolver resolver,
        IClaimTransmissionStore transmissions,
        IClaimAttachmentContentStore content,
        IClaimAttachmentTransmissionStore attachmentTransmissions,
        IHostEnvironment environment)
    {
        _resolver = resolver;
        _transmissions = transmissions;
        _content = content;
        _attachmentTransmissions = attachmentTransmissions;
        _environment = environment;
    }

    [HttpPost("claims/{transmissionId}/attachments")]
    [RequestSizeLimit(64 * 1024 * 1024)]
    public async Task<IActionResult> Submit(
        string transmissionId,
        [FromForm] GatewayClaimAttachmentDemoForm form,
        [FromQuery] string? gateway,
        CancellationToken ct)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        var transmission = await _transmissions.GetByIdAsync(transmissionId, ct);
        if (transmission is null)
        {
            return NotFound(new { error = "Transmission not found." });
        }

        var contextTenant = HttpContext.Items["TenantId"]?.ToString();
        if (!string.IsNullOrWhiteSpace(contextTenant) &&
            !string.Equals(contextTenant, transmission.TenantId, StringComparison.Ordinal))
        {
            return BadRequest(new { error = "Tenant does not match the claim transmission." });
        }

        if (form.File is null || form.File.Length <= 0)
        {
            return BadRequest(new { error = "A non-empty file is required." });
        }

        IClaimAttachmentGateway attachments;
        try
        {
            attachments = _resolver.ResolveCapability<IClaimAttachmentGateway>(gateway);
        }
        catch (GatewayCapabilityNotSupportedException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        var attachmentId = string.IsNullOrWhiteSpace(form.AttachmentId)
            ? Guid.NewGuid().ToString("N")
            : form.AttachmentId.Trim();
        var contentType = string.IsNullOrWhiteSpace(form.ContentType)
            ? form.File.ContentType
            : form.ContentType;

        await using var upload = form.File.OpenReadStream();
        var stored = await _content.StoreAsync(
            new ClaimAttachmentStoreRequest
            {
                TenantId = transmission.TenantId,
                TransmissionId = transmission.TransmissionId,
                AttachmentId = attachmentId,
                ContentType = contentType ?? string.Empty,
                DisplayName = form.File.FileName,
                ScanStatus = ClaimAttachmentScanStatus.Unknown
            },
            upload,
            ct);

        var request = new ClaimAttachmentSubmissionRequest
        {
            TenantId = transmission.TenantId,
            ClaimId = transmission.ClaimId,
            TransmissionId = transmission.TransmissionId,
            PayerId = transmission.PayerId,
            AttachmentId = attachmentId,
            AttachmentControlNumber = form.AttachmentControlNumber,
            AttachmentType = form.AttachmentType,
            Mode = form.Mode,
            FileName = stored.DisplayName,
            ContentType = stored.ContentType,
            ContentLength = stored.ContentLength,
            Content = stored,
            ServiceLineNumber = form.ServiceLineNumber,
            Description = form.Description,
            CreatedAt = DateTimeOffset.UtcNow,
            AttachmentVersion = form.AttachmentVersion < 1 ? 1 : form.AttachmentVersion
        };

        var result = await attachments.SubmitAttachmentAsync(request, ct);
        return Ok(result);
    }

    [HttpGet("claims/{transmissionId}/attachments")]
    public async Task<IActionResult> List(string transmissionId, CancellationToken ct)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        var list = await _attachmentTransmissions.ListByClaimTransmissionIdAsync(transmissionId, ct);
        return Ok(list);
    }
}

public sealed class GatewayClaimAttachmentDemoForm
{
    public IFormFile? File { get; set; }

    public string? AttachmentId { get; set; }

    public string? AttachmentControlNumber { get; set; }

    public ClaimAttachmentType AttachmentType { get; set; } = ClaimAttachmentType.Other;

    public ClaimAttachmentMode Mode { get; set; } = ClaimAttachmentMode.Unsolicited;

    public string? ContentType { get; set; }

    public int? ServiceLineNumber { get; set; }

    public string? Description { get; set; }

    public int AttachmentVersion { get; set; } = 1;
}
