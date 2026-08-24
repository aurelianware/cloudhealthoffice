using System.Text.Json.Serialization;

namespace CloudHealthOffice.Infrastructure.Gateways.Stedi.Models;

/// <summary>
/// Stedi Create Claim Attachment (275) JSON request.
/// POST https://claims.us.stedi.com/2025-03-07/claim-attachments/file
/// </summary>
internal sealed class StediCreateClaimAttachmentRequestDto
{
    [JsonPropertyName("contentType")]
    public string ContentType { get; set; } = string.Empty;
}

internal sealed class StediCreateClaimAttachmentResponseDto
{
    [JsonPropertyName("attachmentId")]
    public string? AttachmentId { get; set; }

    /// <summary>
    /// Pre-signed PUT URL. Stedi documents both <c>uploadUrl</c> and
    /// <c>uploadURL</c>; case-insensitive JSON options bind either.
    /// </summary>
    [JsonPropertyName("uploadUrl")]
    public string? UploadUrl { get; set; }
}
